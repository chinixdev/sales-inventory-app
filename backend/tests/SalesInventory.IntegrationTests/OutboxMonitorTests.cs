using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.IntegrationTests;

/// <summary>Monitor de eventos del Outbox (solo Admin) contra SQL Server real.</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class OutboxMonitorTests(SqlServerConcurrencyDatabaseFixture database)
{
    private async Task ResetAsync()
    {
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.ProcessedEvents.ExecuteDeleteAsync();
    }

    private async Task<int> AuthenticateAsAdminAsync()
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));
        return seeded.UserId;
    }

    private async Task<Guid> InsertAsync(
        OutboxMessageStatus status,
        string eventType = "SaleCompleted.v1",
        string payload = "{}",
        string? lastError = null,
        int retryCount = 0,
        DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? DateTime.UtcNow;
        var message = OutboxMessage.CreatePending(Guid.NewGuid(), eventType, payload, occurred);
        message.Status = status;
        message.RetryCount = retryCount;
        message.LastError = lastError;
        if (status == OutboxMessageStatus.Processed)
        {
            message.ProcessedAtUtc = occurred.AddSeconds(1);
        }

        if (status == OutboxMessageStatus.Processing)
        {
            message.LockId = Guid.NewGuid();
            message.LockedUntilUtc = DateTime.UtcNow.AddMinutes(-5); // lease vencido
        }

        await using var context = database.CreateDbContext();
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        return message.Id;
    }

    // ─── Autorización ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/outbox/summary")]
    [InlineData("GET", "/api/outbox/messages")]
    [InlineData("GET", "/api/outbox/messages/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/outbox/messages/00000000-0000-0000-0000-000000000001/requeue")]
    public async Task MonitorEndpoints_AreForbiddenForSellers_AndRequireAuthentication(string method, string path)
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));
        using var seller = await database.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        using var anonymousClient = database.CreateAnonymousClient();
        using var anonymous = await anonymousClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        seller.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Resumen y listado ───────────────────────────────────────────────────

    [Fact]
    public async Task Summary_CountsEventsByStatus_AndFlagsExpiredLeases()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        await InsertAsync(OutboxMessageStatus.Pending, occurredAtUtc: DateTime.UtcNow.AddMinutes(-30));
        await InsertAsync(OutboxMessageStatus.Pending);
        await InsertAsync(OutboxMessageStatus.Processing);
        await InsertAsync(OutboxMessageStatus.Processed);
        await InsertAsync(OutboxMessageStatus.Processed);
        await InsertAsync(OutboxMessageStatus.Processed);
        await InsertAsync(OutboxMessageStatus.Failed);

        var summary = await database.Client.GetFromJsonAsync<OutboxSummaryDto>("/api/outbox/summary");

        summary!.Pending.Should().Be(2);
        summary.Processing.Should().Be(1);
        summary.Processed.Should().Be(3);
        summary.Failed.Should().Be(1);
        summary.ExpiredLeases.Should().Be(1);
        summary.OldestPendingAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-30), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task List_FiltersByStatusTypeAndDate_AndPaginates()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        var old = DateTime.UtcNow.AddDays(-10);
        await InsertAsync(OutboxMessageStatus.Failed, "SaleCompleted.v1");
        await InsertAsync(OutboxMessageStatus.Failed, "Other.v1");
        await InsertAsync(OutboxMessageStatus.Processed, "SaleCompleted.v1");
        var oldPending = await InsertAsync(OutboxMessageStatus.Pending, "SaleCompleted.v1", occurredAtUtc: old);

        var failed = await GetPageAsync("?status=Failed");
        failed.Items.Should().HaveCount(2).And.OnlyContain(i => i.Status == "Failed");

        var failedSales = await GetPageAsync("?status=failed&eventType=SaleCompleted.v1");
        failedSales.Items.Should().ContainSingle().Which.EventType.Should().Be("SaleCompleted.v1");

        var recent = await GetPageAsync($"?from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"))}");
        recent.Items.Should().NotContain(i => i.Id == oldPending);

        var olderOnly = await GetPageAsync($"?to={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-5).ToString("o"))}");
        olderOnly.Items.Should().ContainSingle().Which.Id.Should().Be(oldPending);

        var firstPage = await GetPageAsync("?pageSize=1&pageNumber=1");
        firstPage.Items.Should().ContainSingle();
        firstPage.TotalItems.Should().Be(4);
        firstPage.TotalPages.Should().Be(4);

        using var invalid = await database.Client.GetAsync("/api/outbox/messages?status=Bogus");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<PagedResult<OutboxMessageListItemDto>> GetPageAsync(string query) =>
        (await database.Client.GetFromJsonAsync<PagedResult<OutboxMessageListItemDto>>($"/api/outbox/messages{query}"))!;

    // ─── Detalle seguro ──────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_ExposesSafeTechnicalInformationOnly()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        var id = await InsertAsync(
            OutboxMessageStatus.Failed,
            payload: "{\"saleId\":42,\"items\":[{\"productId\":1,\"quantity\":1},{\"productId\":2,\"quantity\":3}],\"secret\":\"top-secret-value\"}",
            lastError: "InvalidOperationException: fallo con Password=Sup3rS3cret en la conexión\nsegunda línea",
            retryCount: 3);
        await using (var context = database.CreateDbContext())
        {
            context.ProcessedEvents.Add(new ProcessedEvent { EventId = id, Consumer = "c1", EventType = "SaleCompleted.v1", ProcessedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        using var response = await database.Client.GetAsync($"/api/outbox/messages/{id}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain("top-secret-value").And.NotContain("Sup3rS3cret");
        raw.Should().NotContainEquivalentOf("payload\"").And.NotContainEquivalentOf("lockId");
        var detail = System.Text.Json.JsonSerializer.Deserialize<OutboxMessageDetailDto>(
            raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        detail.Status.Should().Be("Failed");
        detail.RetryCount.Should().Be(3);
        detail.SaleId.Should().Be(42);
        detail.ItemCount.Should().Be(2);
        detail.PayloadBytes.Should().BeGreaterThan(0);
        detail.LastError.Should().Contain("[redacted]").And.NotContain("\n");
        detail.Consumers.Should().ContainSingle().Which.Consumer.Should().Be("c1");

        using var missing = await database.Client.GetAsync($"/api/outbox/messages/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Reprocesamiento manual ──────────────────────────────────────────────

    [Fact]
    public async Task Requeue_MovesOnlyFailedEventsToPending_KeepingEventIdAndProcessedEvents()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        var failedId = await InsertAsync(OutboxMessageStatus.Failed, lastError: "boom", retryCount: 5);
        var pendingId = await InsertAsync(OutboxMessageStatus.Pending);
        var processedId = await InsertAsync(OutboxMessageStatus.Processed);
        await using (var context = database.CreateDbContext())
        {
            context.ProcessedEvents.Add(new ProcessedEvent { EventId = failedId, Consumer = "c1", EventType = "SaleCompleted.v1", ProcessedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        using var ok = await database.Client.PostAsync($"/api/outbox/messages/{failedId}/requeue", null);
        using var alreadyPending = await database.Client.PostAsync($"/api/outbox/messages/{pendingId}/requeue", null);
        using var processed = await database.Client.PostAsync($"/api/outbox/messages/{processedId}/requeue", null);
        using var unknown = await database.Client.PostAsync($"/api/outbox/messages/{Guid.NewGuid()}/requeue", null);

        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        alreadyPending.StatusCode.Should().Be(HttpStatusCode.Conflict);
        processed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var check = database.CreateDbContext();
        var requeued = await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == failedId);
        requeued.Status.Should().Be(OutboxMessageStatus.Pending);
        requeued.RetryCount.Should().Be(0);
        requeued.LastError.Should().BeNull();
        requeued.LockId.Should().BeNull();
        requeued.NextAttemptAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        (await check.ProcessedEvents.CountAsync(p => p.EventId == failedId)).Should().Be(1, "no se eliminan los ProcessedEvents");
        (await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == processedId)).Status
            .Should().Be(OutboxMessageStatus.Processed);
    }

    [Fact]
    public async Task ConcurrentRequeues_OfTheSameFailedEvent_ApplyTheTransitionExactlyOnce()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        var id = await InsertAsync(OutboxMessageStatus.Failed, retryCount: 5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => database.Client.PostAsync($"/api/outbox/messages/{id}/requeue", null)));
        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var check = database.CreateDbContext();
        (await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id)).Status.Should().Be(OutboxMessageStatus.Pending);
    }
}
