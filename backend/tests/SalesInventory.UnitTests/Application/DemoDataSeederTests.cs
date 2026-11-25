using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Demo;
using SalesInventory.Infrastructure.Persistence;
using SalesInventory.Infrastructure.Security;

namespace SalesInventory.UnitTests.Application;

public sealed class DemoDataSeederTests
{
    private const string SellerPassword = "demo-seller-pass-123";
    private const string AdminPassword = "demo-admin-pass-456";

    private static SalesInventoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SalesInventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DemoDataSeeder CreateSeeder(SalesInventoryDbContext context, DemoOptions options) =>
        new(context, new Pbkdf2PasswordHasher(), Options.Create(options), NullLogger<DemoDataSeeder>.Instance);

    private static DemoOptions Enabled(string? adminPassword = AdminPassword) =>
        new() { Enabled = true, SellerPassword = SellerPassword, AdminPassword = adminPassword };

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        await using var context = CreateContext();

        await CreateSeeder(context, new DemoOptions { Enabled = false }).SeedAsync();

        (await context.Products.CountAsync()).Should().Be(0);
        (await context.Users.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seeds_Catalog_Users_AndSales()
    {
        await using var context = CreateContext();

        await CreateSeeder(context, Enabled()).SeedAsync();

        (await context.Products.CountAsync()).Should().BeGreaterThan(10);
        (await context.Sales.CountAsync()).Should().BeGreaterThan(10);
        var users = await context.Users.ToListAsync();
        users.Select(u => u.Role).Should().BeEquivalentTo(new[] { UserRole.Seller, UserRole.Admin });
        users.Should().OnlyContain(u => u.PasswordHash != SellerPassword && u.PasswordHash != AdminPassword);
    }

    [Fact]
    public async Task WithoutAdminPassword_NoAdminIsCreated()
    {
        await using var context = CreateContext();

        await CreateSeeder(context, Enabled(adminPassword: null)).SeedAsync();

        (await context.Users.SingleAsync()).Role.Should().Be(UserRole.Seller);
    }

    [Fact]
    public async Task SeededStock_NeverGoesNegative_AndMatchesTheMovementLedger()
    {
        await using var context = CreateContext();

        await CreateSeeder(context, Enabled()).SeedAsync();

        var movements = await context.StockMovements.ToListAsync();
        foreach (var product in await context.Products.ToListAsync())
        {
            var own = movements.Where(m => m.ProductId == product.Id).ToList();
            var entered = own.Where(m => m.MovementType == StockMovementType.IN).Sum(m => m.Quantity);
            var sold = own.Where(m => m.MovementType == StockMovementType.OUT).Sum(m => m.Quantity);
            product.Stock.Should().BeGreaterThanOrEqualTo(0);
            product.Stock.Should().Be(entered - sold, product.SKU);
        }

        var sales = await context.Sales.Include(s => s.SaleDetails).ToListAsync();
        sales.Should().OnlyContain(s => s.Total == s.SaleDetails.Sum(d => d.Subtotal) && s.Total > 0);
    }

    [Fact]
    public async Task SeedingTwice_DoesNotDuplicateAnything()
    {
        await using var context = CreateContext();
        var seeder = CreateSeeder(context, Enabled());

        await seeder.SeedAsync();
        var products = await context.Products.CountAsync();
        var sales = await context.Sales.CountAsync();
        await seeder.SeedAsync();

        (await context.Products.CountAsync()).Should().Be(products);
        (await context.Sales.CountAsync()).Should().Be(sales);
    }

    [Fact]
    public async Task ExistingData_IsNeverOverwritten()
    {
        await using var context = CreateContext();
        context.Categories.Add(new SalesInventory.Domain.Entities.Category { Name = "Real" });
        await context.SaveChangesAsync();
        context.Products.Add(new SalesInventory.Domain.Entities.Product { CategoryId = 1, SKU = "REAL-1", Name = "Producto real", Price = 1m });
        await context.SaveChangesAsync();

        await CreateSeeder(context, Enabled()).SeedAsync();

        (await context.Products.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("corta")]
    public async Task AWeakOrMissingSellerPassword_IsRejected(string? password)
    {
        await using var context = CreateContext();

        var act = async () => await CreateSeeder(context, new DemoOptions { Enabled = true, SellerPassword = password }).SeedAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Demo:SellerPassword*");
    }
}
