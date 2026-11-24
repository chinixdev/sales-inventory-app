using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace SalesInventory.IntegrationTests;

[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class ConcurrentInventoryOperationsTests
{
    private readonly SqlServerConcurrencyDatabaseFixture _database;

    public ConcurrentInventoryOperationsTests(SqlServerConcurrencyDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task TwoSalesForTheLastUnit_OnlyOneSucceedsAndReturns409ForTheOther()
    {
        var seeded = await _database.SeedProductsAsync(1);
        _database.SynchronizeNextProductReads(2);

        var responses = await Task.WhenAll(
            CreateSaleAsync(seeded.UserId, (seeded.ProductIds[0], 1)),
            CreateSaleAsync(seeded.UserId, (seeded.ProductIds[0], 1)));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        await using var context = _database.CreateDbContext();
        (await context.Products.Where(product => product.Id == seeded.ProductIds[0])
            .Select(product => product.Stock).SingleAsync()).Should().Be(0);
        var sale = await context.Sales.SingleAsync(sale => sale.UserId == seeded.UserId);
        (await context.SaleDetails.CountAsync(detail => detail.SaleId == sale.Id)).Should().Be(1);
        var movement = await context.StockMovements.SingleAsync(
            movement => movement.ProductId == seeded.ProductIds[0]);
        movement.PreviousStock.Should().Be(1);
        movement.NewStock.Should().Be(0);
        movement.Quantity.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentMultiProductSales_AreAtomicAcrossAllProducts()
    {
        var seeded = await _database.SeedProductsAsync(1, 1);
        _database.SynchronizeNextProductReads(2);

        var saleItems = seeded.ProductIds.Select(id => (id, 1)).ToArray();
        var responses = await Task.WhenAll(
            CreateSaleAsync(seeded.UserId, saleItems),
            CreateSaleAsync(seeded.UserId, saleItems));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        await using var context = _database.CreateDbContext();
        var products = await context.Products
            .Where(product => seeded.ProductIds.Contains(product.Id))
            .OrderBy(product => product.Id)
            .ToListAsync();
        products.Select(product => product.Stock).Should().Equal(0, 0);
        var sale = await context.Sales.SingleAsync(sale => sale.UserId == seeded.UserId);
        (await context.SaleDetails.CountAsync(detail => detail.SaleId == sale.Id)).Should().Be(2);
        var movements = await context.StockMovements
            .Where(movement => seeded.ProductIds.Contains(movement.ProductId))
            .ToListAsync();
        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(movement =>
            movement.PreviousStock == 1
            && movement.NewStock == 0
            && movement.Quantity == 1);
    }

    [Fact]
    public async Task ConcurrentEntries_DoNotLoseStockUpdates()
    {
        var seeded = await _database.SeedProductsAsync(0);
        _database.SynchronizeNextProductReads(2);

        var request = new
        {
            productId = seeded.ProductIds[0],
            userId = seeded.UserId,
            quantity = 5,
            reason = "Concurrent replenishment"
        };
        var responses = await Task.WhenAll(
            _database.Client.PostAsJsonAsync("/api/inventory/entries", request),
            _database.Client.PostAsJsonAsync("/api/inventory/entries", request));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        await using var context = _database.CreateDbContext();
        (await context.Products.Where(product => product.Id == seeded.ProductIds[0])
            .Select(product => product.Stock).SingleAsync()).Should().Be(5);
        var movement = await context.StockMovements.SingleAsync(
            movement => movement.ProductId == seeded.ProductIds[0]);
        movement.PreviousStock.Should().Be(0);
        movement.NewStock.Should().Be(5);
        movement.Quantity.Should().Be(5);
    }

    [Fact]
    public async Task ConcurrentAdjustments_KeepProductAndMovementBalancesConsistent()
    {
        var seeded = await _database.SeedProductsAsync(10);
        _database.SynchronizeNextProductReads(2);

        var responses = await Task.WhenAll(
            CreateAdjustmentAsync(seeded, 2),
            CreateAdjustmentAsync(seeded, 5));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        await using var context = _database.CreateDbContext();
        var productStock = await context.Products
            .Where(product => product.Id == seeded.ProductIds[0])
            .Select(product => product.Stock)
            .SingleAsync();
        var movement = await context.StockMovements.SingleAsync(
            movement => movement.ProductId == seeded.ProductIds[0]);
        movement.PreviousStock.Should().Be(10);
        movement.NewStock.Should().Be(productStock);
        movement.Quantity.Should().Be(Math.Abs(10 - productStock));
    }

    [Fact]
    public async Task FailedMovementInsert_RollsBackSaleDetailsAndStock()
    {
        var seeded = await _database.SeedProductsAsync(4);
        _database.FailNextMovementInsert();

        var response = await CreateSaleAsync(seeded.UserId, (seeded.ProductIds[0], 2));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using var context = _database.CreateDbContext();
        (await context.Sales.CountAsync(sale => sale.UserId == seeded.UserId)).Should().Be(0);
        (await context.SaleDetails.CountAsync(detail => detail.ProductId == seeded.ProductIds[0]))
            .Should().Be(0);
        (await context.StockMovements.CountAsync(movement => movement.ProductId == seeded.ProductIds[0]))
            .Should().Be(0);
        (await context.Products.Where(product => product.Id == seeded.ProductIds[0])
            .Select(product => product.Stock).SingleAsync()).Should().Be(4);
    }

    private Task<HttpResponseMessage> CreateSaleAsync(
        int userId,
        params (int ProductId, int Quantity)[] items)
    {
        var request = new
        {
            userId,
            items = items.Select(item => new
            {
                productId = item.ProductId,
                quantity = item.Quantity
            })
        };

        return _database.PostSaleAsync(request);
    }

    private Task<HttpResponseMessage> CreateAdjustmentAsync(
        SqlServerConcurrencyDatabaseFixture.SeededProducts seeded,
        int newStock)
    {
        return _database.Client.PostAsJsonAsync("/api/inventory/adjustments", new
        {
            productId = seeded.ProductIds[0],
            userId = seeded.UserId,
            newStock,
            reason = $"Concurrent adjustment to {newStock}"
        });
    }
}
