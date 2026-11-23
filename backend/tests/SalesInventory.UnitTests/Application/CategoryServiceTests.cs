using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Categories.DTOs;
using SalesInventory.Application.Categories.Services;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class CategoryServiceTests
{
    [Fact]
    public async Task CreateAsync_TrimsNameAndReturnsCategory()
    {
        await using var context = CreateContext();
        var service = new CategoryService(context);

        var result = await service.CreateAsync(new CreateCategoryRequestDto { Name = "  Papelería  " });

        result.Name.Should().Be("Papelería");
        result.IsActive.Should().BeTrue();
        (await context.Categories.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhenNameAlreadyExists_ThrowsConflict()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Name = "Papelería" });
        await context.SaveChangesAsync();
        var service = new CategoryService(context);

        var act = () => service.CreateAsync(new CreateCategoryRequestDto { Name = " papelería " });

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task GetByIdAsync_WhenCategoryIsMissing_ThrowsNotFound()
    {
        await using var context = CreateContext();
        var service = new CategoryService(context);

        var act = () => service.GetByIdAsync(404);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_WhenCategoryHasProducts_ThrowsConflict()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Papelería" };
        context.Categories.Add(category);
        context.Products.Add(new Product
        {
            Category = category,
            SKU = "PEN-001",
            Name = "Bolígrafo",
            Price = 1.25m
        });
        await context.SaveChangesAsync();
        var service = new CategoryService(context);

        var act = () => service.DeleteAsync(category.Id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    private static SalesInventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SalesInventoryDbContext(options);
    }
}
