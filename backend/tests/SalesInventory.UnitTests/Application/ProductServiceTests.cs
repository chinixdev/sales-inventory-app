using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Products.DTOs;
using SalesInventory.Application.Products.Services;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class ProductServiceTests
{
    [Fact]
    public async Task CreateAsync_WithInitialStock_PersistsMatchingStockMovement()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        var user = new User
        {
            FullName = "Test User",
            Email = "test@example.com",
            PasswordHash = "test-hash",
            Role = UserRole.Admin
        };
        context.AddRange(category, user);
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var result = await service.CreateAsync(new CreateProductRequestDto
        {
            CategoryId = category.Id,
            SKU = " pen-001 ",
            Name = "Bolígrafo",
            Price = 1.25m,
            InitialStock = 12,
            UserId = user.Id
        });

        result.Stock.Should().Be(12);
        result.SKU.Should().Be("PEN-001");
        var movement = await context.StockMovements.SingleAsync();
        movement.MovementType.Should().Be(StockMovementType.IN);
        movement.Quantity.Should().Be(12);
        movement.PreviousStock.Should().Be(0);
        movement.NewStock.Should().Be(12);
        movement.ProductId.Should().Be(result.Id);
    }

    [Fact]
    public async Task CreateAsync_WhenCategoryDoesNotExist_ThrowsNotFound()
    {
        await using var context = CreateContext();
        var service = new ProductService(context);

        var act = () => service.CreateAsync(new CreateProductRequestDto
        {
            CategoryId = 999,
            SKU = "PEN-001",
            Name = "Bolígrafo",
            Price = 1.25m
        });

        await act.Should().ThrowAsync<NotFoundException>();
        (await context.Products.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WithInitialStockAndNoActiveAdministrator_RejectsRequestWithoutCreatingUser()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var act = () => service.CreateAsync(new CreateProductRequestDto
        {
            CategoryId = category.Id,
            SKU = "PEN-001",
            Name = "Bolígrafo",
            Price = 1.25m,
            InitialStock = 5
        });

        await act.Should().ThrowAsync<ValidationException>();
        (await context.Users.CountAsync()).Should().Be(0);
        (await context.Products.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WithInitialStockAndInactiveUser_ThrowsNotFound()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        var user = new User
        {
            FullName = "Inactive User",
            Email = "inactive@example.com",
            PasswordHash = "test-hash",
            IsActive = false
        };
        context.AddRange(category, user);
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var act = () => service.CreateAsync(new CreateProductRequestDto
        {
            CategoryId = category.Id,
            SKU = "PEN-001",
            Name = "Bolígrafo",
            Price = 1.25m,
            InitialStock = 5,
            UserId = user.Id
        });

        await act.Should().ThrowAsync<NotFoundException>();
        (await context.Products.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_WhenSkuAlreadyExists_ThrowsConflict()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        context.Categories.Add(category);
        context.Products.Add(new Product
        {
            Category = category,
            SKU = "PEN-001",
            Name = "Bolígrafo existente",
            Price = 1.25m
        });
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var act = () => service.CreateAsync(new CreateProductRequestDto
        {
            CategoryId = category.Id,
            SKU = "pen-001",
            Name = "Otro bolígrafo",
            Price = 1.50m
        });

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task UpdateAsync_DoesNotChangeStock()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        var product = new Product
        {
            Category = category,
            SKU = "PEN-001",
            Name = "Bolígrafo",
            Price = 1.25m,
            Stock = 12
        };
        context.AddRange(category, product);
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var result = await service.UpdateAsync(product.Id, new UpdateProductRequestDto
        {
            CategoryId = category.Id,
            SKU = "PEN-001",
            Name = "Bolígrafo actualizado",
            Price = 1.50m,
            MinimumStock = 3
        });

        result.Stock.Should().Be(12);
        (await context.Products.SingleAsync()).Stock.Should().Be(12);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersBySearchAndAppliesPagination()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        context.AddRange(
            category,
            new Product { Category = category, SKU = "PEN-001", Name = "Bolígrafo azul", Price = 1m },
            new Product { Category = category, SKU = "PEN-002", Name = "Bolígrafo negro", Price = 1m },
            new Product { Category = category, SKU = "NOTE-001", Name = "Cuaderno", Price = 2m });
        await context.SaveChangesAsync();
        var service = new ProductService(context);

        var result = await service.GetPagedAsync(new ProductFilterParams
        {
            SearchTerm = "bolígrafo",
            PageNumber = 1,
            PageSize = 1
        });

        result.TotalItems.Should().Be(2);
        result.Items.Should().ContainSingle();
        result.TotalPages.Should().Be(2);
    }

    private static SalesInventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SalesInventoryDbContext(options);
    }
}
