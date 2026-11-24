using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Domain.Enums;

namespace SalesInventory.IntegrationTests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();
        scope.Client.DefaultRequestHeaders.Authorization = null;

        var response = await scope.Client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

}

[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SellerAuthorizationTests
{
    private readonly SqlServerConcurrencyDatabaseFixture _database;

    public SellerAuthorizationTests(SqlServerConcurrencyDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Seller_CanReadAndCreateSales_ButCannotWriteCatalogOrInventory()
    {
        var seeded = await _database.SeedProductsAsync(2);
        _database.AuthenticateAs(seeded.UserId, UserRole.Seller.ToString());

        var categories = await _database.Client.GetAsync("/api/categories");
        categories.StatusCode.Should().Be(HttpStatusCode.OK);
        var products = await _database.Client.GetAsync("/api/products");
        products.StatusCode.Should().Be(HttpStatusCode.OK);

        _database.AuthenticateAs(seeded.UserId, "Other");
        var unrecognizedRoleRead = await _database.Client.GetAsync("/api/categories");
        unrecognizedRoleRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _database.AuthenticateAs(seeded.UserId, UserRole.Seller.ToString());
        var categoryWrite = await _database.Client.PostAsJsonAsync(
            "/api/categories",
            new { name = $"Seller-{Guid.NewGuid():N}" });
        categoryWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var inventoryWrite = await _database.Client.PostAsJsonAsync(
            "/api/inventory/entries",
            new
            {
                productId = seeded.ProductIds[0],
                userId = int.MaxValue,
                quantity = 1,
                reason = "Role authorization test"
            });
        inventoryWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var sale = await _database.PostSaleAsync(
            new
            {
                userId = int.MaxValue,
                items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } }
            });
        sale.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var context = _database.CreateDbContext();
        var persistedSale = await context.Sales.SingleAsync(sale => sale.UserId == seeded.UserId);
        persistedSale.UserId.Should().Be(seeded.UserId);
        var movement = await context.StockMovements.SingleAsync(
            movement => movement.ProductId == seeded.ProductIds[0]
                && movement.SaleId == persistedSale.Id);
        movement.UserId.Should().Be(seeded.UserId);
    }
}
