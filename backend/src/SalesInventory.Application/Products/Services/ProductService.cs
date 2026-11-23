using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Products.DTOs;
using SalesInventory.Application.Products.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.Application.Products.Services;

public class ProductService : IProductService
{
    private readonly IApplicationDbContext _context;

    public ProductService(IApplicationDbContext _context)
    {
        this._context = _context;
    }

    public async Task<PagedResult<ProductResponseDto>> GetPagedAsync(ProductFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = _context.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .AsQueryable();

        // 1. Filtro de búsqueda por término (SKU o Nombre)
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var search = filter.SearchTerm.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(search) || p.SKU.ToLower().Contains(search));
        }

        // 2. Filtro por categoría
        if (filter.CategoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == filter.CategoryId.Value);
        }

        // 3. Filtro por estado activo/inactivo
        if (filter.ActiveOnly.HasValue)
        {
            query = query.Where(p => p.IsActive == filter.ActiveOnly.Value);
        }

        // 4. Filtro por stock bajo (alerta de reabastecimiento)
        if (filter.LowStockOnly == true)
        {
            query = query.Where(p => p.Stock <= p.MinimumStock);
        }

        var totalItems = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.Name)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(p => new ProductResponseDto
            {
                Id = p.Id,
                CategoryId = p.CategoryId,
                CategoryName = p.Category != null ? p.Category.Name : string.Empty,
                SKU = p.SKU,
                Name = p.Name,
                Description = p.Description,
                Price = p.Price,
                Stock = p.Stock,
                MinimumStock = p.MinimumStock,
                IsActive = p.IsActive,
                IsBelowMinimumStock = p.Stock <= p.MinimumStock,
                CreatedAt = p.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductResponseDto>(items, totalItems, filter.PageNumber, filter.PageSize);
    }

    public async Task<ProductResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.Id == id)
            .Select(p => new ProductResponseDto
            {
                Id = p.Id,
                CategoryId = p.CategoryId,
                CategoryName = p.Category != null ? p.Category.Name : string.Empty,
                SKU = p.SKU,
                Name = p.Name,
                Description = p.Description,
                Price = p.Price,
                Stock = p.Stock,
                MinimumStock = p.MinimumStock,
                IsActive = p.IsActive,
                IsBelowMinimumStock = p.Stock <= p.MinimumStock,
                CreatedAt = p.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (product == null)
        {
            throw new NotFoundException(nameof(Product), id);
        }

        return product;
    }

    public async Task<ProductResponseDto> CreateAsync(CreateProductRequestDto request, CancellationToken cancellationToken = default)
    {
        // 1. Validar que la categoría exista
        var category = await _context.Categories
            .FirstOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);

        if (category == null)
        {
            throw new NotFoundException(nameof(Category), request.CategoryId);
        }

        // 2. Validar unicidad de SKU
        var normalizedSku = request.SKU.Trim().ToUpperInvariant();
        var skuExists = await _context.Products
            .AnyAsync(p => p.SKU == normalizedSku, cancellationToken);

        if (skuExists)
        {
            throw new ConflictException($"Ya existe un producto registrado con el SKU '{normalizedSku}'.");
        }

        // 3. Crear entidad Producto
        var product = new Product
        {
            CategoryId = request.CategoryId,
            SKU = normalizedSku,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Price = request.Price,
            Stock = request.InitialStock,
            MinimumStock = request.MinimumStock,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Products.Add(product);

        // 4. Registrar stock inicial mediante un movimiento de inventario de forma transaccional
        if (request.InitialStock > 0)
        {
            var auditUserId = await ResolveAuditUserIdAsync(request.UserId, cancellationToken);

            var initialMovement = new StockMovement
            {
                Product = product,
                UserId = auditUserId,
                MovementType = StockMovementType.IN,
                Quantity = request.InitialStock,
                PreviousStock = 0,
                NewStock = request.InitialStock,
                Reason = "Stock inicial por alta de producto",
                SaleId = null,
                CreatedAt = DateTime.UtcNow
            };

            initialMovement.ValidateBalance();
            _context.StockMovements.Add(initialMovement);
        }

        // 5. Guardar producto y movimiento en una sola transacción atómica
        await _context.SaveChangesAsync(cancellationToken);

        return new ProductResponseDto
        {
            Id = product.Id,
            CategoryId = product.CategoryId,
            CategoryName = category.Name,
            SKU = product.SKU,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Stock = product.Stock,
            MinimumStock = product.MinimumStock,
            IsActive = product.IsActive,
            IsBelowMinimumStock = product.Stock <= product.MinimumStock,
            CreatedAt = product.CreatedAt
        };
    }

    public async Task<ProductResponseDto> UpdateAsync(int id, UpdateProductRequestDto request, CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product == null)
        {
            throw new NotFoundException(nameof(Product), id);
        }

        // 1. Validar categoría
        if (product.CategoryId != request.CategoryId)
        {
            var categoryExists = await _context.Categories
                .AnyAsync(c => c.Id == request.CategoryId, cancellationToken);

            if (!categoryExists)
            {
                throw new NotFoundException(nameof(Category), request.CategoryId);
            }
        }

        // 2. Validar unicidad de SKU
        var normalizedSku = request.SKU.Trim().ToUpperInvariant();
        var skuDuplicate = await _context.Products
            .AnyAsync(p => p.Id != id && p.SKU == normalizedSku, cancellationToken);

        if (skuDuplicate)
        {
            throw new ConflictException($"Ya existe otro producto con el código SKU '{normalizedSku}'.");
        }

        // 3. Actualizar datos permitidos
        // NOTA: El stock NO se modifica directamente en este endpoint (Regla de negocio crítica)
        product.CategoryId = request.CategoryId;
        product.SKU = normalizedSku;
        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim();
        product.Price = request.Price;
        product.MinimumStock = request.MinimumStock;
        product.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);

        var categoryName = await _context.Categories
            .Where(c => c.Id == product.CategoryId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new ProductResponseDto
        {
            Id = product.Id,
            CategoryId = product.CategoryId,
            CategoryName = categoryName,
            SKU = product.SKU,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Stock = product.Stock,
            MinimumStock = product.MinimumStock,
            IsActive = product.IsActive,
            IsBelowMinimumStock = product.Stock <= product.MinimumStock,
            CreatedAt = product.CreatedAt
        };
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product == null)
        {
            throw new NotFoundException(nameof(Product), id);
        }

        var hasHistoricalRecords = await _context.SaleDetails.AnyAsync(sd => sd.ProductId == id, cancellationToken)
                                   || await _context.StockMovements.AnyAsync(sm => sm.ProductId == id, cancellationToken);

        if (hasHistoricalRecords)
        {
            // Borrado lógico para proteger la trazabilidad de auditoría
            product.IsActive = false;
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            _context.Products.Remove(product);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<int> ResolveAuditUserIdAsync(int? requestedUserId, CancellationToken cancellationToken)
    {
        if (requestedUserId.HasValue)
        {
            var userExists = await _context.Users
                .AnyAsync(
                    u => u.Id == requestedUserId.Value && u.IsActive,
                    cancellationToken);

            if (!userExists)
                throw new NotFoundException(nameof(User), requestedUserId.Value);

            return requestedUserId.Value;
        }

        var administratorId = await _context.Users
            .Where(user => user.Role == UserRole.Admin && user.IsActive)
            .OrderBy(user => user.Id)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return administratorId
            ?? throw new ValidationException(
                "Se requiere un administrador activo para registrar el stock inicial.");
    }
}
