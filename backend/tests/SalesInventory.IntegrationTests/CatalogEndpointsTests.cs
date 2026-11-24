using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using Xunit;

namespace SalesInventory.IntegrationTests;

public sealed class CatalogEndpointsTests
{
    [Fact]
    public async Task OpenApiAndScalar_AreAvailableInDevelopment()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();

        var openApi = await scope.Client.GetAsync("/openapi/v1.json");
        openApi.StatusCode.Should().Be(HttpStatusCode.OK);
        var specification = await openApi.Content.ReadAsStringAsync();
        specification.Should().Contain("/api/Categories");
        specification.Should().Contain("/api/Products");

        var scalar = await scope.Client.GetAsync("/scalar/v1");
        scalar.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Categories_CrudAndBusinessConflicts_ReturnExpectedHttpStatuses()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();
        var name = $"Category-{Guid.NewGuid():N}";

        var invalidCreate = await scope.Client.PostAsJsonAsync("/api/categories", new { name = "x" });
        invalidCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var create = await scope.Client.PostAsJsonAsync("/api/categories", new { name });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<int>();

        var duplicate = await scope.Client.PostAsJsonAsync("/api/categories", new { name });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var get = await scope.Client.GetAsync($"/api/categories/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = await scope.Client.PutAsJsonAsync(
            $"/api/categories/{id}",
            new { name = $"{name}-updated", isActive = false });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await scope.Client.DeleteAsync($"/api/categories/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var missing = await scope.Client.GetAsync("/api/categories/2147483647");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Products_CreateWithInitialStock_RecordsMovementAndUpdateCannotChangeStock()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();
        var category = await CreateCategoryAsync(scope);
        var auditUser = new User
        {
            FullName = "Integration Test User",
            Email = $"catalog-{Guid.NewGuid():N}@example.com",
            PasswordHash = "test-only-hash",
            Role = UserRole.Admin,
            IsActive = true
        };
        await using (var context = scope.CreateContext())
        {
            context.Users.Add(auditUser);
            await context.SaveChangesAsync();
        }

        var sku = $"E2E-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var create = await scope.Client.PostAsJsonAsync("/api/products", new
        {
            categoryId = category.Id,
            sku,
            name = "Integration Product",
            description = "Created by API integration test",
            price = 12.50m,
            initialStock = 13,
            minimumStock = 3,
            userId = auditUser.Id
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        create.Headers.Location.Should().NotBeNull();
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var productId = created!["id"]!.GetValue<int>();
        created["stock"]!.GetValue<int>().Should().Be(13);

        await using (var context = scope.CreateContext())
        {
            var product = await context.Products.SingleAsync(p => p.Id == productId);
            var movement = await context.StockMovements.SingleAsync(m => m.ProductId == productId);
            product.Stock.Should().Be(13);
            movement.MovementType.Should().Be(StockMovementType.IN);
            movement.Quantity.Should().Be(13);
            movement.PreviousStock.Should().Be(0);
            movement.NewStock.Should().Be(13);
            movement.UserId.Should().Be(scope.UserId);
        }

        var update = await scope.Client.PutAsJsonAsync($"/api/products/{productId}", new
        {
            categoryId = category.Id,
            sku,
            name = "Updated Product",
            description = "Stock cannot be changed here",
            price = 15m,
            minimumStock = 2,
            isActive = true,
            stock = 999
        });

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<JsonObject>();
        updated!["stock"]!.GetValue<int>().Should().Be(13);

        var list = await scope.Client.GetFromJsonAsync<JsonObject>(
            $"/api/products?searchTerm={sku}&pageNumber=1&pageSize=5");
        list!["totalItems"]!.GetValue<int>().Should().Be(1);
        list["items"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Products_ValidationNotFoundAndDuplicateSku_ReturnExpectedHttpStatuses()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();
        var invalid = await scope.Client.PostAsJsonAsync("/api/products", new
        {
            categoryId = 0,
            sku = "x",
            name = "x",
            price = 0
        });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missingCategory = await scope.Client.PostAsJsonAsync("/api/products", new
        {
            categoryId = 2147483647,
            sku = "MISSING-001",
            name = "Missing category",
            price = 1m
        });
        missingCategory.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missingProduct = await scope.Client.GetAsync("/api/products/2147483647");
        missingProduct.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var category = await CreateCategoryAsync(scope);
        var sku = $"DUP-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var product = new
        {
            categoryId = category.Id,
            sku,
            name = "Duplicate test product",
            price = 1m,
            initialStock = 0
        };
        (await scope.Client.PostAsJsonAsync("/api/products", product)).StatusCode
            .Should().Be(HttpStatusCode.Created);
        var duplicate = await scope.Client.PostAsJsonAsync("/api/products", product);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var categoryDelete = await scope.Client.DeleteAsync($"/api/categories/{category.Id}");
        categoryDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Products_WhenMovementInsertFails_DoesNotPersistProduct()
    {
        await using var scope = await SqlServerApiScope.CreateAsync();
        var category = await CreateCategoryAsync(scope);
        var user = new User
        {
            FullName = "Transaction Test User",
            Email = $"transaction-{Guid.NewGuid():N}@example.com",
            PasswordHash = "test-only-hash",
            Role = UserRole.Admin,
            IsActive = true
        };

        await using (var context = scope.CreateContext())
        {
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        var sku = $"FAIL-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        scope.FailNextStockMovementInsert();

        var response = await scope.Client.PostAsJsonAsync("/api/products", new
        {
            categoryId = category.Id,
            sku,
            name = "Must not persist",
            price = 1m,
            initialStock = 1,
            userId = user.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using var verificationContext = scope.CreateContext();
        (await verificationContext.Products.AnyAsync(p => p.SKU == sku)).Should().BeFalse();
        (await (from movement in verificationContext.StockMovements
                join product in verificationContext.Products on movement.ProductId equals product.Id
                where product.SKU == sku
                select movement.Id).AnyAsync()).Should().BeFalse();
    }

    private static async Task<CategoryRecord> CreateCategoryAsync(SqlServerApiScope scope)
    {
        var name = $"Category-{Guid.NewGuid():N}";
        var response = await scope.Client.PostAsJsonAsync("/api/categories", new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return new CategoryRecord(body!["id"]!.GetValue<int>(), name);
    }

    private sealed record CategoryRecord(int Id, string Name);
}
