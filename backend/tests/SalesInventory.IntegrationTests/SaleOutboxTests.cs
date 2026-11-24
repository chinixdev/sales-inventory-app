using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.IntegrationTests;

/// <summary>Transactional Outbox de ventas contra SQL Server real (base de datos aislada por fixture).</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SaleOutboxTests(SqlServerConcurrencyDatabaseFixture database)
{
    private static object SaleBody(int productId, int quantity) =>
        new { items = new[] { new { productId, quantity } } };

    /// <summary>Mensajes del Outbox cuyo contrato pertenece al usuario indicado (cada prueba siembra su propio usuario).</summary>
    private async Task<List<(OutboxMessage Message, JsonElement Payload)>> OutboxOfUserAsync(int userId)
    {
        await using var context = database.CreateDbContext();
        var all = await context.OutboxMessages.AsNoTracking().ToListAsync();
        return all
            .Select(message => (Message: message, Payload: JsonDocument.Parse(message.Payload).RootElement.Clone()))
            .Where(pair => pair.Payload.GetProperty("userId").GetInt32() == userId)
            .ToList();
    }

    private async Task<int> StockOfAsync(int productId)
    {
        await using var context = database.CreateDbContext();
        return await context.Products.Where(p => p.Id == productId).Select(p => p.Stock).SingleAsync();
    }

    [Fact]
    public async Task SuccessfulSale_PersistsExactlyOneExplicitVersionedEvent()
    {
        var seeded = await database.SeedProductsAsync(5);

        using var response = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 2));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponseDto>();
        var events = await OutboxOfUserAsync(seeded.UserId);
        events.Should().ContainSingle();

        var (message, payload) = events[0];
        message.EventType.Should().Be(SaleCompletedV1.EventType);
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.RetryCount.Should().Be(0);
        message.ProcessedAtUtc.Should().BeNull();
        message.LockId.Should().BeNull();
        message.LockedUntilUtc.Should().BeNull();
        message.LastError.Should().BeNull();
        message.RowVersion.Should().NotBeNullOrEmpty();
        message.NextAttemptAtUtc.Should().Be(message.OccurredAtUtc);

        payload.GetProperty("schemaVersion").GetInt32().Should().Be(SaleCompletedV1.CurrentSchemaVersion);
        payload.GetProperty("eventId").GetGuid().Should().Be(message.Id);
        payload.GetProperty("saleId").GetInt32().Should().Be(sale!.Id);
        payload.GetProperty("total").GetDecimal().Should().Be(sale.Total);
        payload.GetProperty("occurredAtUtc").GetString().Should().EndWith("Z");
        var items = payload.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("productId").GetInt32().Should().Be(seeded.ProductIds[0]);
        items[0].GetProperty("quantity").GetInt32().Should().Be(2);
        message.Payload.Should().NotContainEquivalentOf("rowVersion", "el payload es un contrato explícito, no una entidad EF");
    }

    [Fact]
    public async Task FailedSale_DoesNotCreateAnyEvent()
    {
        var seeded = await database.SeedProductsAsync(1);

        using var response = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 5));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await OutboxOfUserAsync(seeded.UserId)).Should().BeEmpty();
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(1);
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotDuplicateTheEvent()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 2);

        using var first = await database.PostSaleAsync(body, key);
        using var replay = await database.PostSaleAsync(body, key);
        using var conflicting = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 3), key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        conflicting.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await OutboxOfUserAsync(seeded.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task DifferentKeys_ProduceOneEventPerSale()
    {
        var seeded = await database.SeedProductsAsync(5);
        var body = SaleBody(seeded.ProductIds[0], 1);

        using var first = await database.PostSaleAsync(body, Guid.NewGuid());
        using var second = await database.PostSaleAsync(body, Guid.NewGuid());

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var events = await OutboxOfUserAsync(seeded.UserId);
        events.Should().HaveCount(2);
        events.Select(e => e.Payload.GetProperty("saleId").GetInt32()).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task SimultaneousRequestsWithTheSameKey_ProduceASingleEvent()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var seeded = await database.SeedProductsAsync(5);
            var key = Guid.NewGuid();
            var body = SaleBody(seeded.ProductIds[0], 1);

            var responses = await Task.WhenAll(
                database.PostSaleAsync(body, key),
                database.PostSaleAsync(body, key),
                database.PostSaleAsync(body, key));
            try
            {
                responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
                var events = await OutboxOfUserAsync(seeded.UserId);
                events.Should().ContainSingle("solo la solicitud ganadora confirma venta y evento");
                (await StockOfAsync(seeded.ProductIds[0])).Should().Be(4);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task OutboxPersistenceFailure_RollsBackTheWholeSale_AndRetryWithSameKeySucceeds()
    {
        var seeded = await database.SeedProductsAsync(4);
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 2);

        database.FailNextOutboxInsert();
        using var failed = await database.PostSaleAsync(body, key);

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using (var context = database.CreateDbContext())
        {
            (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(0);
            (await context.SaleDetails.CountAsync(d => d.ProductId == seeded.ProductIds[0])).Should().Be(0);
            (await context.StockMovements.CountAsync(m => m.ProductId == seeded.ProductIds[0])).Should().Be(0);
            (await context.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(0);
        }

        (await OutboxOfUserAsync(seeded.UserId)).Should().BeEmpty();
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(4);

        using var retry = await database.PostSaleAsync(body, key);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OutboxOfUserAsync(seeded.UserId)).Should().ContainSingle();
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(2);
    }

    [Fact]
    public async Task OutboxTable_EnforcesJsonPayloadAndStatusConstraints()
    {
        await using var context = database.CreateDbContext();
        context.OutboxMessages.Add(OutboxMessage.CreatePending(Guid.NewGuid(), "Test.v1", "esto no es json", DateTime.UtcNow));

        var invalidPayload = async () => await context.SaveChangesAsync();

        await invalidPayload.Should().ThrowAsync<DbUpdateException>();

        await using var other = database.CreateDbContext();
        var message = OutboxMessage.CreatePending(Guid.NewGuid(), "Test.v1", "{}", DateTime.UtcNow);
        message.Status = OutboxMessageStatus.Processed; // Processed exige ProcessedAtUtc
        other.OutboxMessages.Add(message);

        var inconsistentState = async () => await other.SaveChangesAsync();

        await inconsistentState.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task OutboxMessage_UsesRowVersionForOptimisticConcurrency()
    {
        var id = Guid.NewGuid();
        await using (var seed = database.CreateDbContext())
        {
            seed.OutboxMessages.Add(OutboxMessage.CreatePending(id, "Test.v1", "{}", DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var a = await first.OutboxMessages.SingleAsync(m => m.Id == id);
        var b = await second.OutboxMessages.SingleAsync(m => m.Id == id);
        a.Status = OutboxMessageStatus.Processing;
        a.LockId = Guid.NewGuid();
        a.LockedUntilUtc = DateTime.UtcNow.AddMinutes(1);
        await first.SaveChangesAsync();
        b.RetryCount = 1;

        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Migration_CreatesTheFilteredIndexesForPendingProcessing()
    {
        await using var context = database.CreateDbContext();

        var filtered = await context.Database
            .SqlQueryRaw<string>(
                "SELECT name AS Value FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.OutboxMessages') AND has_filter = 1")
            .ToListAsync();

        filtered.Should().BeEquivalentTo("IX_OutboxMessages_Pending", "IX_OutboxMessages_ProcessingLease");
    }
}
