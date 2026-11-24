using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Outbox;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class StockAlertsHandlerTests
{
    private static SalesInventoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed record Scenario(SalesInventoryDbContext Context, User Admin, User Seller, Product Product, OutboxEnvelope Envelope);

    private static async Task<Scenario> ArrangeAsync(int stock, int minimumStock, bool productActive = true)
    {
        var context = CreateContext();
        var category = new Category { Name = "Cat" };
        var admin = new User { FullName = "Admin", Email = "a@x.pe", PasswordHash = "h", Role = UserRole.Admin, IsActive = true };
        var seller = new User { FullName = "Seller", Email = "s@x.pe", PasswordHash = "h", Role = UserRole.Seller, IsActive = true };
        var product = new Product
        {
            Category = category, SKU = "P-1", Name = "Teclado", Price = 10m, Stock = stock,
            MinimumStock = minimumStock, IsActive = productActive
        };
        context.AddRange(category, admin, seller, product);
        await context.SaveChangesAsync();

        var sale = new Sale { Id = 9, UserId = seller.Id, Total = 10m, SaleDate = DateTime.UtcNow };
        var message = SaleCompletedV1.ToOutboxMessage(sale, [new SaleDetail { ProductId = product.Id, Quantity = 1, UnitPrice = 10m }]);
        var envelope = new OutboxEnvelope(message.Id, message.EventType, message.Payload, message.OccurredAtUtc);
        return new Scenario(context, admin, seller, product, envelope);
    }

    [Fact]
    public async Task LowStock_NotifiesActiveAdminsOnly_WithCurrentStockAndThreshold()
    {
        var s = await ArrangeAsync(stock: 3, minimumStock: 5);
        var handler = new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System);

        await handler.HandleAsync(s.Envelope, CancellationToken.None);

        var notification = await s.Context.Notifications.SingleAsync();
        notification.UserId.Should().Be(s.Admin.Id);
        notification.Type.Should().Be(NotificationType.LowStock);
        notification.ProductId.Should().Be(s.Product.Id);
        notification.CurrentStock.Should().Be(3);
        notification.MinimumStock.Should().Be(5);
        notification.SourceEventId.Should().Be(s.Envelope.EventId);
        notification.ReadAtUtc.Should().BeNull();
        (await s.Context.ProcessedEvents.CountAsync(p => p.Consumer == SaleStockAlertsOutboxHandler.Consumer)).Should().Be(1);
    }

    [Fact]
    public async Task ZeroStock_IsReportedAsOutOfStock()
    {
        var s = await ArrangeAsync(stock: 0, minimumStock: 5);

        await new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System).HandleAsync(s.Envelope, CancellationToken.None);

        (await s.Context.Notifications.SingleAsync()).Type.Should().Be(NotificationType.OutOfStock);
    }

    [Theory]
    [InlineData(6, 5)]   // por encima del umbral
    [InlineData(100, 0)]
    public async Task StockAboveThreshold_CreatesNoNotification_ButRecordsTheEventAsProcessed(int stock, int minimum)
    {
        var s = await ArrangeAsync(stock, minimum);

        await new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System).HandleAsync(s.Envelope, CancellationToken.None);

        (await s.Context.Notifications.CountAsync()).Should().Be(0);
        (await s.Context.ProcessedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task InactiveProduct_IsIgnored()
    {
        var s = await ArrangeAsync(stock: 0, minimumStock: 5, productActive: false);

        await new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System).HandleAsync(s.Envelope, CancellationToken.None);

        (await s.Context.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RepeatedDelivery_IsIdempotent()
    {
        var s = await ArrangeAsync(stock: 1, minimumStock: 5);
        var handler = new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System);

        await handler.HandleAsync(s.Envelope, CancellationToken.None);
        await handler.HandleAsync(s.Envelope, CancellationToken.None);
        await handler.HandleAsync(s.Envelope, CancellationToken.None);

        (await s.Context.Notifications.CountAsync()).Should().Be(1);
        (await s.Context.ProcessedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnUnreadAlertOfTheSameTypeAndProduct_IsNotDuplicatedByAnotherEvent()
    {
        var s = await ArrangeAsync(stock: 1, minimumStock: 5);
        var handler = new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System);
        await handler.HandleAsync(s.Envelope, CancellationToken.None);

        var second = SaleCompletedV1.ToOutboxMessage(
            new Sale { Id = 10, UserId = s.Seller.Id, Total = 10m, SaleDate = DateTime.UtcNow },
            [new SaleDetail { ProductId = s.Product.Id, Quantity = 1, UnitPrice = 10m }]);
        await handler.HandleAsync(new OutboxEnvelope(second.Id, second.EventType, second.Payload, second.OccurredAtUtc), CancellationToken.None);

        (await s.Context.Notifications.CountAsync()).Should().Be(1);

        (await s.Context.Notifications.SingleAsync()).ReadAtUtc = DateTime.UtcNow;
        await s.Context.SaveChangesAsync();
        var third = SaleCompletedV1.ToOutboxMessage(
            new Sale { Id = 11, UserId = s.Seller.Id, Total = 10m, SaleDate = DateTime.UtcNow },
            [new SaleDetail { ProductId = s.Product.Id, Quantity = 1, UnitPrice = 10m }]);
        await handler.HandleAsync(new OutboxEnvelope(third.Id, third.EventType, third.Payload, third.OccurredAtUtc), CancellationToken.None);

        (await s.Context.Notifications.CountAsync()).Should().Be(2, "tras leer la anterior, una nueva alerta sí es válida");
    }

    [Fact]
    public async Task InvalidPayload_IsAPermanentError()
    {
        var s = await ArrangeAsync(stock: 1, minimumStock: 5);
        var broken = new OutboxEnvelope(Guid.NewGuid(), SaleCompletedV1.EventType, "{\"foo\":1}", DateTime.UtcNow);

        var act = async () => await new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System).HandleAsync(broken, CancellationToken.None);

        await act.Should().ThrowAsync<PermanentOutboxException>();
        (await s.Context.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dispatcher_RunsEveryConsumerRegisteredForTheEvent()
    {
        var s = await ArrangeAsync(stock: 1, minimumStock: 5);
        var dispatcher = new OutboxDispatcher([
            new SaleCompletedOutboxHandler(s.Context, TimeProvider.System),
            new SaleStockAlertsOutboxHandler(s.Context, TimeProvider.System)]);

        await dispatcher.DispatchAsync(s.Envelope, CancellationToken.None);

        (await s.Context.ProcessedEvents.Select(p => p.Consumer).ToListAsync()).Should().BeEquivalentTo(
            SaleCompletedOutboxHandler.Consumer, SaleStockAlertsOutboxHandler.Consumer);
        (await s.Context.Notifications.CountAsync()).Should().Be(1);
    }
}
