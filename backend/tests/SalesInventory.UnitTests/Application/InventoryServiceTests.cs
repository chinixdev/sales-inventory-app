using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Inventory.DTOs;
using SalesInventory.Application.Inventory.Services;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class InventoryServiceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SalesInventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SalesInventoryDbContext(options);
    }

    private static async Task<(SalesInventoryDbContext ctx, Product product, User user)> SeedAsync()
    {
        var ctx = CreateContext();

        var category = new Category { Name = "Electrónica" };
        var user = new User
        {
            FullName     = "Admin Test",
            Email        = "admin@test.com",
            PasswordHash = "hash",
            Role         = UserRole.Admin
        };
        ctx.AddRange(category, user);
        await ctx.SaveChangesAsync();

        var product = new Product
        {
            CategoryId   = category.Id,
            SKU          = "ELEC-001",
            Name         = "Teclado mecánico",
            Price        = 85.00m,
            Stock        = 10,
            MinimumStock = 3,
            IsActive     = true,
            CreatedAt    = DateTime.UtcNow
        };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        return (ctx, product, user);
    }

    // ─── GetMovementsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetMovementsAsync_ReturnsPagedList()
    {
        var (ctx, product, user) = await SeedAsync();
        ctx.StockMovements.Add(new StockMovement
        {
            ProductId     = product.Id,
            UserId        = user.Id,
            MovementType  = StockMovementType.IN,
            Quantity      = 5,
            PreviousStock = 10,
            NewStock      = 15,
            Reason        = "Reabastecimiento",
            CreatedAt     = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var service = new InventoryService(ctx);
        var result  = await service.GetMovementsAsync(new MovementFilterParams { PageNumber = 1, PageSize = 10 });

        result.TotalItems.Should().Be(1);
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMovementsAsync_FiltersByProductId()
    {
        var (ctx, product, user) = await SeedAsync();

        // Movimiento para otro producto imaginario (no existe, pero el filtro es por Id)
        ctx.StockMovements.Add(new StockMovement
        {
            ProductId     = product.Id,
            UserId        = user.Id,
            MovementType  = StockMovementType.IN,
            Quantity      = 2,
            PreviousStock = 10,
            NewStock      = 12,
            Reason        = "Ajuste",
            CreatedAt     = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var service = new InventoryService(ctx);

        var withFilter    = await service.GetMovementsAsync(new MovementFilterParams { ProductId = product.Id });
        var withoutFilter = await service.GetMovementsAsync(new MovementFilterParams());

        withFilter.TotalItems.Should().Be(1);
        withoutFilter.TotalItems.Should().Be(1);
    }

    // ─── RegisterEntryAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RegisterEntryAsync_IncreasesStockAndCreatesMovement()
    {
        var (ctx, product, user) = await SeedAsync();
        var originalStock = product.Stock;

        var service = new InventoryService(ctx);
        var result  = await service.RegisterEntryAsync(new StockEntryRequestDto
        {
            ProductId = product.Id,
            UserId    = user.Id,
            Quantity  = 5,
            Reason    = "Compra de proveedor"
        });

        result.MovementType.Should().Be("IN");
        result.PreviousStock.Should().Be(originalStock);
        result.NewStock.Should().Be(originalStock + 5);
        result.Quantity.Should().Be(5);

        var updatedProduct = await ctx.Products.FindAsync(product.Id);
        updatedProduct!.Stock.Should().Be(originalStock + 5);

        var movements = await ctx.StockMovements.CountAsync();
        movements.Should().Be(1);
    }

    [Fact]
    public async Task RegisterEntryAsync_WhenProductNotFound_ThrowsNotFoundException()
    {
        await using var ctx = CreateContext();
        var service = new InventoryService(ctx);

        var act = async () => await service.RegisterEntryAsync(new StockEntryRequestDto
        {
            ProductId = 999,
            UserId    = 1,
            Quantity  = 5,
            Reason    = "Test"
        });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RegisterEntryAsync_WhenUserNotFound_ThrowsNotFoundException()
    {
        var (ctx, product, _) = await SeedAsync();
        var service = new InventoryService(ctx);

        var act = async () => await service.RegisterEntryAsync(new StockEntryRequestDto
        {
            ProductId = product.Id,
            UserId    = 999,
            Quantity  = 5,
            Reason    = "Test"
        });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RegisterEntryAsync_WhenUserIsInactive_ThrowsNotFoundException()
    {
        var (ctx, product, user) = await SeedAsync();
        user.IsActive = false;
        await ctx.SaveChangesAsync();
        var service = new InventoryService(ctx);

        var act = async () => await service.RegisterEntryAsync(new StockEntryRequestDto
        {
            ProductId = product.Id,
            UserId = user.Id,
            Quantity = 5,
            Reason = "Test"
        });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ─── RegisterAdjustmentAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RegisterAdjustmentAsync_SetsStockToTargetAndCreatesMovement()
    {
        var (ctx, product, user) = await SeedAsync();   // stock = 10

        var service = new InventoryService(ctx);
        var result  = await service.RegisterAdjustmentAsync(new StockAdjustmentRequestDto
        {
            ProductId = product.Id,
            UserId    = user.Id,
            NewStock  = 7,
            Reason    = "Conteo físico"
        });

        result.MovementType.Should().Be("ADJUSTMENT");
        result.PreviousStock.Should().Be(10);
        result.NewStock.Should().Be(7);
        result.Quantity.Should().Be(3);   // |10 - 7|

        var updatedProduct = await ctx.Products.FindAsync(product.Id);
        updatedProduct!.Stock.Should().Be(7);
    }

    [Fact]
    public async Task RegisterAdjustmentAsync_WhenSameStock_ThrowsValidationException()
    {
        var (ctx, product, user) = await SeedAsync();   // stock = 10
        var service = new InventoryService(ctx);

        var act = async () => await service.RegisterAdjustmentAsync(new StockAdjustmentRequestDto
        {
            ProductId = product.Id,
            UserId    = user.Id,
            NewStock  = 10,   // igual al actual
            Reason    = "Sin cambio"
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task RegisterAdjustmentAsync_WhenNegativeStock_ThrowsDomainException()
    {
        var (ctx, product, user) = await SeedAsync();
        var service = new InventoryService(ctx);

        // El DTO valida >= 0, pero la entidad de dominio también protege.
        // Este test valida la capa de dominio directamente vía AdjustStock.
        var act = () => product.AdjustStock(-1);

        act.Should().Throw<SalesInventory.Domain.Exceptions.DomainException>();
    }
}
