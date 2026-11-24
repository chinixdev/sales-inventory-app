using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Categories.DTOs;
using SalesInventory.Application.Categories.Interfaces;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Categories.Services;

public class CategoryService : ICategoryService
{
    private readonly IApplicationDbContext _context;

    public CategoryService(IApplicationDbContext _context)
    {
        this._context = _context;
    }

    public async Task<IReadOnlyList<CategoryResponseDto>> GetAllAsync(bool? activeOnly = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Categories.AsNoTracking();

        if (activeOnly.HasValue)
        {
            query = query.Where(c => c.IsActive == activeOnly.Value);
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new CategoryResponseDto
            {
                Id = c.Id,
                Name = c.Name,
                IsActive = c.IsActive,
                ProductCount = c.Products.Count
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<CategoryResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var category = await _context.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CategoryResponseDto
            {
                Id = c.Id,
                Name = c.Name,
                IsActive = c.IsActive,
                ProductCount = c.Products.Count
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (category == null)
        {
            throw new NotFoundException(nameof(Category), id);
        }

        return category;
    }

    public async Task<CategoryResponseDto> CreateAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        var trimmedName = request.Name.Trim();

        var exists = await _context.Categories
            .AnyAsync(c => c.Name.ToLower() == trimmedName.ToLower(), cancellationToken);

        if (exists)
        {
            throw new ConflictException($"Ya existe una categoría con el nombre '{trimmedName}'.");
        }

        var category = new Category
        {
            Name = trimmedName,
            IsActive = true
        };

        _context.Categories.Add(category);
        await _context.SaveChangesAsync(cancellationToken);

        return new CategoryResponseDto
        {
            Id = category.Id,
            Name = category.Name,
            IsActive = category.IsActive,
            ProductCount = 0
        };
    }

    public async Task<CategoryResponseDto> UpdateAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        var category = await _context.Categories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category == null)
        {
            throw new NotFoundException(nameof(Category), id);
        }

        var trimmedName = request.Name.Trim();

        var duplicateExists = await _context.Categories
            .AnyAsync(c => c.Id != id && c.Name.ToLower() == trimmedName.ToLower(), cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException($"Ya existe otra categoría con el nombre '{trimmedName}'.");
        }

        category.Name = trimmedName;
        category.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        var productCount = await _context.Products
            .CountAsync(p => p.CategoryId == id, cancellationToken);

        return new CategoryResponseDto
        {
            Id = category.Id,
            Name = category.Name,
            IsActive = category.IsActive,
            ProductCount = productCount
        };
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var category = await _context.Categories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category == null)
        {
            throw new NotFoundException(nameof(Category), id);
        }

        var hasProducts = await _context.Products
            .AnyAsync(p => p.CategoryId == id, cancellationToken);

        if (hasProducts)
        {
            throw new ConflictException($"No es posible eliminar la categoría '{category.Name}' porque contiene productos asociados. Considere desactivarla (IsActive = false).");
        }

        _context.Categories.Remove(category);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
