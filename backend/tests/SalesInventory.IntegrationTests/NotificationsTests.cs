using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Notifications;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.IntegrationTests;

/// <summary>Notificaciones de stock generadas por SaleCompleted.v1 y su API, contra SQL Server real.</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class NotificationsTests(SqlServerConcurrencyDatabaseFixture database)
{
    private async Task ResetAsync()
    {
        await using var context = database.CreateDbContext();
        await context.Notifications.ExecuteDeleteAsync();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.ProcessedEvents.ExecuteDeleteAsync();
    }

    /// <summary>Procesa el Outbox con los handlers reales (SaleCompleted y alertas de stock).</summary>
    private async Task ProcessOutboxAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SalesInventoryDbContext>(options =>
            options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SalesInventoryDbContext>());
        services.AddOutboxProcessing(options => options.BatchSize = 50);
        await using var provider = services.BuildServiceProvider();

        await using (var context = database.CreateDbContext())
        {
            var due = DateTime.UtcNow.AddMinutes(-1);
            await context.OutboxMessages
                .Where(m => m.Status == OutboxMessageStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, due));
        }

        var processor = provider.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    private async Task<(int AdminId, int ProductId)> SeedLowStockScenarioAsync(int stock, int minimumStock)
    {
        var seeded = await database.SeedProductsAsync(stock);
        await using var context = database.CreateDbContext();
        await context.Products.Where(p => p.Id == seeded.ProductIds[0])
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.MinimumStock, minimumStock));
        return (seeded.UserId, seeded.ProductIds[0]);
    }

    private async Task SellAsync(int productId, int quantity)
    {
        using var response = await database.PostSaleAsync(
            new { items = new[] { new { productId, quantity } } });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<List<NotificationResponseDto>> NotificationsOfAsync(int userId, string query = "")
    {
        database.AuthenticateAs(userId, nameof(UserRole.Admin));
        var page = await database.Client.GetFromJsonAsync<PagedResult<NotificationResponseDto>>(
            $"/api/notifications?pageSize=100{query}");
        return page!.Items.ToList();
    }

    private async Task<int> UnreadCountAsync(int userId, string role = nameof(UserRole.Admin))
    {
        database.AuthenticateAs(userId, role);
        var count = await database.Client.GetFromJsonAsync<UnreadCountDto>("/api/notifications/unread-count");
        return count!.Count;
    }

    [Fact]
    public async Task LowStockSale_CreatesAPersistentNotification_WithCurrentStockAndThreshold()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 6, minimumStock: 5);
        await SellAsync(productId, 2); // 6 → 4 (≤ 5)

        await ProcessOutboxAsync();

        var notifications = await NotificationsOfAsync(adminId);
        var alert = notifications.Should().ContainSingle(n => n.ProductId == productId).Subject;
        alert.Type.Should().Be("LowStock");
        alert.CurrentStock.Should().Be(4);
        alert.MinimumStock.Should().Be(5);
        alert.IsRead.Should().BeFalse();
        alert.ReadAtUtc.Should().BeNull();
        alert.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        (await UnreadCountAsync(adminId)).Should().Be(1);
    }

    [Fact]
    public async Task SaleThatSellsOutTheProduct_CreatesAnOutOfStockNotification()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 2, minimumStock: 1);
        await SellAsync(productId, 2); // 2 → 0

        await ProcessOutboxAsync();

        var alert = (await NotificationsOfAsync(adminId)).Should().ContainSingle(n => n.ProductId == productId).Subject;
        alert.Type.Should().Be("OutOfStock");
        alert.CurrentStock.Should().Be(0);
    }

    [Fact]
    public async Task ProductAboveItsThreshold_GeneratesNoNotification()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 20, minimumStock: 5);
        await SellAsync(productId, 2); // 18 > 5

        await ProcessOutboxAsync();

        (await NotificationsOfAsync(adminId)).Should().BeEmpty();
    }

    [Fact]
    public async Task StaleAlert_IsNotEmitted_WhenTheProductWasRestockedBeforeProcessing()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 6, minimumStock: 5);
        await SellAsync(productId, 2); // al vender queda en 4: sería stock bajo

        await using (var context = database.CreateDbContext())
        {
            await context.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 50));
        }

        await ProcessOutboxAsync();

        (await NotificationsOfAsync(adminId)).Should().BeEmpty("se evalúa el stock vigente, no el de la venta");
        await using var check = database.CreateDbContext();
        (await check.ProcessedEvents.CountAsync(p => p.Consumer == SaleStockAlertsOutboxHandler.Consumer))
            .Should().Be(1, "el evento queda registrado como procesado aunque no genere alertas");
    }

    [Fact]
    public async Task RepeatedDelivery_DoesNotDuplicateNotifications()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 6, minimumStock: 5);
        await SellAsync(productId, 2);
        await ProcessOutboxAsync();

        // Reentrega del mismo evento (p. ej. reencolado manual).
        await using (var context = database.CreateDbContext())
        {
            await context.OutboxMessages.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
                .SetProperty(m => m.ProcessedAtUtc, (DateTime?)null));
        }

        await ProcessOutboxAsync();

        (await NotificationsOfAsync(adminId)).Where(n => n.ProductId == productId).Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnreadAlertOfTheSameTypeAndProduct_IsNotAccumulated_UntilItIsRead()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 10, minimumStock: 9);
        await SellAsync(productId, 1);
        await ProcessOutboxAsync();
        await SellAsync(productId, 1);
        await ProcessOutboxAsync();

        (await NotificationsOfAsync(adminId)).Where(n => n.ProductId == productId).Should().ContainSingle(
            "ya existía una alerta de stock bajo sin leer para ese producto");

        database.AuthenticateAs(adminId, nameof(UserRole.Admin));
        using var read = await database.Client.PostAsync("/api/notifications/read-all", null);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        await SellAsync(productId, 1);
        await ProcessOutboxAsync();

        (await NotificationsOfAsync(adminId)).Where(n => n.ProductId == productId).Should().HaveCount(2);
    }

    [Fact]
    public async Task EachUserOnlySeesAndChangesTheirOwnNotifications()
    {
        await ResetAsync();
        var (adminA, productId) = await SeedLowStockScenarioAsync(stock: 6, minimumStock: 5);
        int adminBId;
        await using (var context = database.CreateDbContext())
        {
            var adminB = new User
            {
                FullName = "Otro administrador",
                Email = $"admin-b-{Guid.NewGuid():N}@example.com",
                PasswordHash = "test-only-hash",
                Role = UserRole.Admin,
                IsActive = true
            };
            context.Users.Add(adminB);
            await context.SaveChangesAsync();
            adminBId = adminB.Id;
        }

        var seller = await database.SeedUserAsync($"seller-n-{Guid.NewGuid():N}@example.com", "Valid-Passphrase-2030");
        database.AuthenticateAs(adminA, nameof(UserRole.Admin));
        await SellAsync(productId, 2);
        await ProcessOutboxAsync();

        var ofA = await NotificationsOfAsync(adminA, "&unread=true");
        var ofB = await NotificationsOfAsync(adminBId);
        ofA.Should().ContainSingle(n => n.ProductId == productId);
        ofB.Should().ContainSingle(n => n.ProductId == productId);
        ofA.Single(n => n.ProductId == productId).Id.Should().NotBe(ofB.Single(n => n.ProductId == productId).Id);

        // B no puede marcar la notificación de A: 404, y la de A sigue sin leer.
        database.AuthenticateAs(adminBId, nameof(UserRole.Admin));
        using var foreign = await database.Client.PostAsync($"/api/notifications/{ofA.Single(n => n.ProductId == productId).Id}/read", null);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UnreadCountAsync(adminA)).Should().Be(1);

        // Un vendedor no recibe alertas de stock y no ve las de otros.
        (await UnreadCountAsync(seller.Id, nameof(UserRole.Seller))).Should().Be(0);
        database.AuthenticateAs(seller.Id, nameof(UserRole.Seller));
        var sellerPage = await database.Client.GetFromJsonAsync<PagedResult<NotificationResponseDto>>("/api/notifications");
        sellerPage!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkAsRead_IsIdempotent_AndReducesTheUnreadCount()
    {
        await ResetAsync();
        var (adminId, productId) = await SeedLowStockScenarioAsync(stock: 6, minimumStock: 5);
        await SellAsync(productId, 2);
        await ProcessOutboxAsync();
        var notification = (await NotificationsOfAsync(adminId)).Single(n => n.ProductId == productId);

        database.AuthenticateAs(adminId, nameof(UserRole.Admin));
        using var first = await database.Client.PostAsync($"/api/notifications/{notification.Id}/read", null);
        using var second = await database.Client.PostAsync($"/api/notifications/{notification.Id}/read", null);
        using var missing = await database.Client.PostAsync("/api/notifications/999999999/read", null);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UnreadCountAsync(adminId)).Should().Be(0);
        var read = (await NotificationsOfAsync(adminId, "&unread=false")).Single(n => n.Id == notification.Id);
        read.IsRead.Should().BeTrue();
        read.ReadAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task InvalidTypeFilter_Returns400()
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));

        using var response = await database.Client.GetAsync("/api/notifications?type=Other");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("GET", "/api/notifications")]
    [InlineData("GET", "/api/notifications/unread-count")]
    [InlineData("POST", "/api/notifications/1/read")]
    [InlineData("POST", "/api/notifications/read-all")]
    public async Task NotificationEndpoints_RequireAuthentication(string method, string path)
    {
        using var client = database.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
