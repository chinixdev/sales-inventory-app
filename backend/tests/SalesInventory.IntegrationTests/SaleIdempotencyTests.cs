using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.IntegrationTests;

/// <summary>Idempotencia de POST /api/sales contra SQL Server real (base de datos aislada por fixture).</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SaleIdempotencyTests(SqlServerConcurrencyDatabaseFixture database)
{
    private static object SaleBody(int productId, int quantity) =>
        new { items = new[] { new { productId, quantity } } };

    private async Task<int> StockOfAsync(int productId)
    {
        await using var context = database.CreateDbContext();
        return await context.Products.Where(p => p.Id == productId).Select(p => p.Stock).SingleAsync();
    }

    [Fact]
    public async Task FirstSale_Succeeds_AndPersistsIdempotencyRecordWithoutSensitiveData()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();

        using var response = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 2), key);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponseDto>();
        await using var context = database.CreateDbContext();
        var record = await context.IdempotencyRecords.SingleAsync(r => r.Key == key);
        record.UserId.Should().Be(seeded.UserId);
        record.Operation.Should().Be(IdempotencyOperations.CreateSale);
        record.SaleId.Should().Be(sale!.Id);
        record.RequestHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(3);
    }

    [Fact]
    public async Task IdenticalRetry_ReturnsOriginalSale_WithoutExtraSaleOrStockMovement()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 2);

        using var first = await database.PostSaleAsync(body, key);
        using var retry = await database.PostSaleAsync(body, key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await first.Content.ReadFromJsonAsync<SaleResponseDto>();
        var replayed = await retry.Content.ReadFromJsonAsync<SaleResponseDto>();
        replayed!.Id.Should().Be(original!.Id);
        replayed.Total.Should().Be(original.Total);
        replayed.Details.Should().HaveCount(1);

        await using var context = database.CreateDbContext();
        (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
        (await context.StockMovements.CountAsync(m => m.ProductId == seeded.ProductIds[0])).Should().Be(1);
        (await context.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(1);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(3);
    }

    [Fact]
    public async Task Retry_AfterStockWasExhausted_StillReturnsTheOriginalSale()
    {
        var seeded = await database.SeedProductsAsync(2);
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 2);

        using var first = await database.PostSaleAsync(body, key);
        using var retry = await database.PostSaleAsync(body, key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(0);
    }

    [Fact]
    public async Task SameKeyWithDifferentContent_Returns409_AndDoesNotChangeData()
    {
        var seeded = await database.SeedProductsAsync(10);
        var key = Guid.NewGuid();

        using var first = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 2), key);
        using var different = await database.PostSaleAsync(SaleBody(seeded.ProductIds[0], 3), key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        different.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var context = database.CreateDbContext();
        (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(8);
    }

    [Fact]
    public async Task DifferentKeys_AreIndependentOperations()
    {
        var seeded = await database.SeedProductsAsync(10);
        var body = SaleBody(seeded.ProductIds[0], 1);

        using var first = await database.PostSaleAsync(body, Guid.NewGuid());
        using var second = await database.PostSaleAsync(body, Guid.NewGuid());

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstSale = await first.Content.ReadFromJsonAsync<SaleResponseDto>();
        var secondSale = await second.Content.ReadFromJsonAsync<SaleResponseDto>();
        secondSale!.Id.Should().NotBe(firstSale!.Id);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(8);
    }

    [Fact]
    public async Task SameKeyUsedByDifferentUsers_CreatesIndependentSales()
    {
        var seeded = await database.SeedProductsAsync(10);
        var otherUser = await database.SeedUserAsync($"idem-{Guid.NewGuid():N}@example.com", "Valid-Passphrase-2030");
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 1);

        using var byFirstUser = await database.PostSaleAsync(body, key);
        database.AuthenticateAs(otherUser.Id, nameof(UserRole.Seller));
        using var bySecondUser = await database.PostSaleAsync(body, key);

        byFirstUser.StatusCode.Should().Be(HttpStatusCode.Created);
        bySecondUser.StatusCode.Should().Be(HttpStatusCode.Created);
        var a = await byFirstUser.Content.ReadFromJsonAsync<SaleResponseDto>();
        var b = await bySecondUser.Content.ReadFromJsonAsync<SaleResponseDto>();
        b!.Id.Should().NotBe(a!.Id);
        b.UserId.Should().Be(otherUser.Id);
        await using var context = database.CreateDbContext();
        (await context.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(2);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(8);
    }

    [Fact]
    public async Task SimultaneousRequestsWithTheSameKey_CreateExactlyOneSale()
    {
        for (var attempt = 0; attempt < 5; attempt++)
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
                var ids = new HashSet<int>();
                foreach (var response in responses)
                {
                    ids.Add((await response.Content.ReadFromJsonAsync<SaleResponseDto>())!.Id);
                }

                ids.Should().HaveCount(1, "todas las solicitudes con la misma clave devuelven la misma venta");
                await using var context = database.CreateDbContext();
                (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
                (await context.StockMovements.CountAsync(m => m.ProductId == seeded.ProductIds[0])).Should().Be(1);
                (await context.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(1);
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
    public async Task FailedSale_RollsBackIdempotencyRecord_AndLaterRetryWithSameKeySucceeds()
    {
        var seeded = await database.SeedProductsAsync(4);
        var key = Guid.NewGuid();
        var body = SaleBody(seeded.ProductIds[0], 2);

        database.FailNextMovementInsert();
        using var failed = await database.PostSaleAsync(body, key);

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using (var context = database.CreateDbContext())
        {
            (await context.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(0);
            (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(0);
        }

        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(4);

        using var retry = await database.PostSaleAsync(body, key);

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var after = database.CreateDbContext();
        (await after.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
        (await after.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(1);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidIdempotencyKey_Returns400_AndCreatesNothing(string? headerValue)
    {
        var seeded = await database.SeedProductsAsync(5);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = JsonContent.Create(SaleBody(seeded.ProductIds[0], 1))
        };
        if (headerValue is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", headerValue);
        }

        using var response = await database.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var context = database.CreateDbContext();
        (await context.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(0);
        (await StockOfAsync(seeded.ProductIds[0])).Should().Be(5);
    }
}
