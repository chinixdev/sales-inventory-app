using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Inventory.DTOs;
using SalesInventory.Application.Inventory.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.Application.Inventory.Services;

public class InventoryService : IInventoryService
{
    private readonly IApplicationDbContext _context;

    public InventoryService(IApplicationDbContext context)
    {
        _context = context;
    }

    // ─── GET /api/inventory/movements ───────────────────────────────────────

    public async Task<PagedResult<StockMovementResponseDto>> GetMovementsAsync(
        MovementFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = _context.StockMovements
            .AsNoTracking()
            .Include(m => m.Product)
            .Include(m => m.User)
            .AsQueryable();

        if (filter.ProductId.HasValue)
            query = query.Where(m => m.ProductId == filter.ProductId.Value);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(m => new StockMovementResponseDto
            {
                Id            = m.Id,
                ProductId     = m.ProductId,
                ProductName   = m.Product != null ? m.Product.Name : string.Empty,
                ProductSKU    = m.Product != null ? m.Product.SKU  : string.Empty,
                UserId        = m.UserId,
                UserFullName  = m.User != null ? m.User.FullName : string.Empty,
                SaleId        = m.SaleId,
                MovementType  = m.MovementType.ToString(),
                Quantity      = m.Quantity,
                PreviousStock = m.PreviousStock,
                NewStock      = m.NewStock,
                Reason        = m.Reason,
                CreatedAt     = m.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<StockMovementResponseDto>(items, total, filter.PageNumber, filter.PageSize);
    }

    // ─── POST /api/inventory/entries ────────────────────────────────────────

    public async Task<StockMovementResponseDto> RegisterEntryAsync(
        StockEntryRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), request.ProductId);

        await ValidateUserExistsAsync(request.UserId, cancellationToken);

        var previousStock = product.Stock;
        product.AddStock(request.Quantity);   // lanza DomainException si quantity <= 0

        var movement = new StockMovement
        {
            ProductId     = product.Id,
            UserId        = request.UserId,
            SaleId        = null,
            MovementType  = StockMovementType.IN,
            Quantity      = request.Quantity,
            PreviousStock = previousStock,
            NewStock      = product.Stock,
            Reason        = request.Reason.Trim(),
            CreatedAt     = DateTime.UtcNow
        };

        movement.ValidateBalance();

        _context.StockMovements.Add(movement);
        await _context.SaveChangesAsync(cancellationToken);   // RowVersion detecta conflictos concurrentes

        return MapToDto(movement, product, request.UserId, null);
    }

    // ─── POST /api/inventory/adjustments ────────────────────────────────────

    public async Task<StockMovementResponseDto> RegisterAdjustmentAsync(
        StockAdjustmentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId && p.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), request.ProductId);

        await ValidateUserExistsAsync(request.UserId, cancellationToken);

        if (request.NewStock == product.Stock)
            throw new Application.Common.Exceptions.ValidationException(
                "El stock final indicado es igual al stock actual; no hay ajuste que realizar.");

        var previousStock = product.Stock;
        var delta = Math.Abs(request.NewStock - previousStock);

        product.AdjustStock(request.NewStock);   // lanza DomainException si newStock < 0

        var movement = new StockMovement
        {
            ProductId     = product.Id,
            UserId        = request.UserId,
            SaleId        = null,
            MovementType  = StockMovementType.ADJUSTMENT,
            Quantity      = delta,
            PreviousStock = previousStock,
            NewStock      = product.Stock,
            Reason        = request.Reason.Trim(),
            CreatedAt     = DateTime.UtcNow
        };

        movement.ValidateBalance();

        _context.StockMovements.Add(movement);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(movement, product, request.UserId, null);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task ValidateUserExistsAsync(int userId, CancellationToken cancellationToken)
    {
        var exists = await _context.Users
            .AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);

        if (!exists)
            throw new NotFoundException(nameof(User), userId);
    }

    private static StockMovementResponseDto MapToDto(
        StockMovement m, Product p, int userId, string? userFullName)
        => new()
        {
            Id            = m.Id,
            ProductId     = m.ProductId,
            ProductName   = p.Name,
            ProductSKU    = p.SKU,
            UserId        = userId,
            UserFullName  = userFullName ?? string.Empty,
            SaleId        = m.SaleId,
            MovementType  = m.MovementType.ToString(),
            Quantity      = m.Quantity,
            PreviousStock = m.PreviousStock,
            NewStock      = m.NewStock,
            Reason        = m.Reason,
            CreatedAt     = m.CreatedAt
        };
}
