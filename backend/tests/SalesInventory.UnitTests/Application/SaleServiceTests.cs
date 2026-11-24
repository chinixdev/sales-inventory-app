using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Services;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class SaleServiceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SalesInventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SalesInventoryDbContext(options);
    }

    private static async Task<(SalesInventoryDbContext ctx, User user, Category cat, Product p1, Product p2)> SeedAsync(
        int p1Stock = 20, int p2Stock = 10)
    {
        var ctx  = CreateContext();
        var cat  = new Category { Name = "Electrónica" };
        var user = new User { FullName = "Vendedor Test", Email = "vendedor@test.com", PasswordHash = "h", Role = UserRole.Admin };
        ctx.AddRange(cat, user);
        await ctx.SaveChangesAsync();

        var p1 = new Product { CategoryId = cat.Id, SKU = "P1", Name = "Teclado", Price = 50m, Stock = p1Stock, MinimumStock = 2, IsActive = true, CreatedAt = DateTime.UtcNow };
        var p2 = new Product { CategoryId = cat.Id, SKU = "P2", Name = "Mouse",   Price = 25m, Stock = p2Stock, MinimumStock = 2, IsActive = true, CreatedAt = DateTime.UtcNow };
        ctx.AddRange(p1, p2);
        await ctx.SaveChangesAsync();

        return (ctx, user, cat, p1, p2);
    }

    // ─── CreateAsync: happy path ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesSaleDetailsAndMovements()
    {
        var (ctx, user, _, p1, p2) = await SeedAsync();
        var service = new SaleService(ctx);

        var result = await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  =
            [
                new SaleItemRequestDto { ProductId = p1.Id, Quantity = 3 },
                new SaleItemRequestDto { ProductId = p2.Id, Quantity = 2 }
            ]
        });

        // Total calculado en backend: 3*50 + 2*25 = 200
        result.Total.Should().Be(200m);
        result.Details.Should().HaveCount(2);

        // Stock descontado correctamente
        var updatedP1 = await ctx.Products.FindAsync(p1.Id);
        var updatedP2 = await ctx.Products.FindAsync(p2.Id);
        updatedP1!.Stock.Should().Be(17);
        updatedP2!.Stock.Should().Be(8);

        // Dos StockMovements tipo OUT creados
        var movements = await ctx.StockMovements.ToListAsync();
        movements.Should().HaveCount(2);
        movements.Should().AllSatisfy(m => m.MovementType.Should().Be(StockMovementType.OUT));
        movements.Should().AllSatisfy(m => m.SaleId.Should().Be(result.Id));
    }

    [Fact]
    public async Task CreateAsync_PriceComesFromDB_IgnoresClientPrice()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        var service = new SaleService(ctx);

        // El request no tiene campo precio, así que el backend toma p1.Price = 50m
        var result = await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = 1 }]
        });

        result.Details.First().UnitPrice.Should().Be(50m);
        result.Total.Should().Be(50m);
    }

    // ─── CreateAsync: validaciones ────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DuplicateProductId_ThrowsValidationException()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  =
            [
                new SaleItemRequestDto { ProductId = p1.Id, Quantity = 2 },
                new SaleItemRequestDto { ProductId = p1.Id, Quantity = 1 }
            ]
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_InsufficientStock_ThrowsInsufficientStockException()
    {
        var (ctx, user, _, p1, _) = await SeedAsync(p1Stock: 2);
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = 10 }]
        });

        await act.Should().ThrowAsync<SalesInventory.Domain.Exceptions.InsufficientStockException>();
    }

    [Fact]
    public async Task CreateAsync_InactiveProduct_ThrowsValidationException()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        p1.IsActive = false;
        await ctx.SaveChangesAsync();
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = 1 }]
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_UserNotFound_ThrowsNotFoundException()
    {
        await using var ctx = CreateContext();
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = 999,
            Items  = [new SaleItemRequestDto { ProductId = 1, Quantity = 1 }]
        });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreateAsync_ProductNotFound_ThrowsNotFoundException()
    {
        var (ctx, user, _, _, _) = await SeedAsync();
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  = [new SaleItemRequestDto { ProductId = 999, Quantity = 1 }]
        });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ─── GetByIdAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_ExistingSale_ReturnsSaleWithDetails()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        var service = new SaleService(ctx);

        var created = await service.CreateAsync(new CreateSaleRequestDto
        {
            IdempotencyKey = Guid.NewGuid(),
            UserId = user.Id,
            Items  = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = 2 }]
        });

        var fetched = await service.GetByIdAsync(created.Id);

        fetched.Id.Should().Be(created.Id);
        fetched.Details.Should().HaveCount(1);
        fetched.Total.Should().Be(100m);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ThrowsNotFoundException()
    {
        await using var ctx = CreateContext();
        var service = new SaleService(ctx);

        var act = async () => await service.GetByIdAsync(9999);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ─── GetPagedAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagedAsync_ReturnsCorrectPage()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        var service = new SaleService(ctx);

        await service.CreateAsync(new CreateSaleRequestDto { UserId = user.Id, Items = [new() { ProductId = p1.Id, Quantity = 1 }] });
        await service.CreateAsync(new CreateSaleRequestDto { UserId = user.Id, Items = [new() { ProductId = p1.Id, Quantity = 1 }] });
        await service.CreateAsync(new CreateSaleRequestDto { UserId = user.Id, Items = [new() { ProductId = p1.Id, Quantity = 1 }] });

        var result = await service.GetPagedAsync(new SaleFilterParams { PageNumber = 1, PageSize = 2 });

        result.TotalItems.Should().Be(3);
        result.Items.Should().HaveCount(2);
        result.TotalPages.Should().Be(2);
    }

    // ─── Idempotencia ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithoutIdempotencyKey_ThrowsValidationException()
    {
        var (ctx, user, _, p1, _) = await SeedAsync();
        var service = new SaleService(ctx);

        var act = async () => await service.CreateAsync(new CreateSaleRequestDto
        {
            UserId = user.Id,
            Items = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = 1 }]
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_SameKeyAndContent_ReturnsOriginalSaleWithoutDeductingStockAgain()
    {
        var (ctx, user, _, p1, _) = await SeedAsync(p1Stock: 10);
        var service = new SaleService(ctx);
        var key = Guid.NewGuid();
        CreateSaleRequestDto NewRequest(int quantity) => new()
        {
            UserId = user.Id,
            IdempotencyKey = key,
            Items = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = quantity }]
        };

        var first = await service.CreateAsync(NewRequest(2));
        var replay = await service.CreateAsync(NewRequest(2));

        replay.Id.Should().Be(first.Id);
        (await ctx.Sales.CountAsync()).Should().Be(1);
        (await ctx.StockMovements.CountAsync()).Should().Be(1);
        (await ctx.Products.AsNoTracking().SingleAsync(p => p.Id == p1.Id)).Stock.Should().Be(8);
    }

    [Fact]
    public async Task CreateAsync_SameKeyDifferentContent_ThrowsConflictException()
    {
        var (ctx, user, _, p1, _) = await SeedAsync(p1Stock: 10);
        var service = new SaleService(ctx);
        var key = Guid.NewGuid();
        CreateSaleRequestDto NewRequest(int quantity) => new()
        {
            UserId = user.Id,
            IdempotencyKey = key,
            Items = [new SaleItemRequestDto { ProductId = p1.Id, Quantity = quantity }]
        };

        await service.CreateAsync(NewRequest(2));
        var act = async () => await service.CreateAsync(NewRequest(3));

        await act.Should().ThrowAsync<ConflictException>();
        (await ctx.Sales.CountAsync()).Should().Be(1);
    }
}
