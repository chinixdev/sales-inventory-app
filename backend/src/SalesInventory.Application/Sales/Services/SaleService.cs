using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.Application.Sales.Services;

public class SaleService : ISaleService
{
    private readonly IApplicationDbContext _context;

    public SaleService(IApplicationDbContext context)
    {
        _context = context;
    }

    // ─── POST /api/sales ─────────────────────────────────────────────────────

    public async Task<SaleResponseDto> CreateAsync(
        CreateSaleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // 1. Validar usuario
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(User), request.UserId);

        // 2. Validar items: colección no vacía
        if (request.Items == null || request.Items.Count == 0)
            throw new ValidationException("La venta debe contener al menos un producto.");

        // 3. Detectar productos duplicados en el request
        var duplicateIds = request.Items
            .GroupBy(i => i.ProductId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
            throw new ValidationException(
                $"El producto con Id {duplicateIds.First()} aparece más de una vez en la venta. Combine las cantidades en un único ítem.");

        // 4. Cargar productos desde BD (nunca confiar en precios del cliente)
        var productIds = request.Items.Select(i => i.ProductId).ToList();
        var products = await _context.Products
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        // 5. Validar que todos los productos existan y estén activos
        foreach (var item in request.Items)
        {
            var product = products.FirstOrDefault(p => p.Id == item.ProductId)
                ?? throw new NotFoundException(nameof(Product), item.ProductId);

            if (!product.IsActive)
                throw new ValidationException(
                    $"El producto '{product.Name}' (Id: {product.Id}) no está activo y no puede venderse.");

            if (item.Quantity <= 0)
                throw new ValidationException(
                    $"La cantidad para el producto '{product.Name}' debe ser mayor a 0.");
        }

        // 6. Construir Sale y SaleDetails — precios y subtotales calculados en backend
        var sale = new Sale
        {
            UserId   = request.UserId,
            SaleDate = DateTime.UtcNow,
            Total    = 0  // se recalcula abajo
        };

        var details = new List<SaleDetail>();
        var movements = new List<StockMovement>();

        foreach (var item in request.Items)
        {
            var product = products.First(p => p.Id == item.ProductId);
            var previousStock = product.Stock;

            // DeductStock lanza InsufficientStockException si no hay stock
            product.DeductStock(item.Quantity);

            var detail = new SaleDetail
            {
                ProductId = product.Id,
                Quantity  = item.Quantity,
                UnitPrice = product.Price,  // precio desde BD
            };
            detail.CalculateSubtotal();     // Subtotal = Quantity * UnitPrice
            details.Add(detail);

            // StockMovement tipo OUT asociado a esta venta
            var movement = new StockMovement
            {
                ProductId     = product.Id,
                UserId        = request.UserId,
                Sale          = sale,
                MovementType  = StockMovementType.OUT,
                Quantity      = item.Quantity,
                PreviousStock = previousStock,
                NewStock      = product.Stock,
                Reason        = $"Venta registrada",
                CreatedAt     = DateTime.UtcNow
            };
            movement.ValidateBalance();
            movements.Add(movement);
        }

        sale.SaleDetails = details;
        sale.StockMovements = movements;
        sale.RecalculateTotal();   // Total = sum(Subtotals)

        if (sale.Total <= 0)
            throw new ValidationException("El total de la venta debe ser mayor a 0.");

        // 7. Guardar venta, detalles, stock y movimientos juntos para conservar la atomicidad
        _context.Sales.Add(sale);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(sale, user.FullName, details, products);
    }

    // ─── GET /api/sales/{id} ─────────────────────────────────────────────────

    public async Task<SaleResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var sale = await _context.Sales
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.SaleDetails)
                .ThenInclude(d => d.Product)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Sale), id);

        return MapSaleEntityToDto(sale);
    }

    // ─── GET /api/sales ──────────────────────────────────────────────────────

    public async Task<PagedResult<SaleResponseDto>> GetPagedAsync(
        SaleFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Sales
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.SaleDetails)
                .ThenInclude(d => d.Product)
            .AsQueryable();

        if (filter.UserId.HasValue)
            query = query.Where(s => s.UserId == filter.UserId.Value);

        if (filter.From.HasValue)
            query = query.Where(s => s.SaleDate >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(s => s.SaleDate <= filter.To.Value);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(s => s.SaleDate)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var dtos = items.Select(MapSaleEntityToDto).ToList();
        return new PagedResult<SaleResponseDto>(dtos, total, filter.PageNumber, filter.PageSize);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SaleResponseDto MapToDto(
        Sale sale,
        string userFullName,
        List<SaleDetail> details,
        List<Product> products)
        => new()
        {
            Id           = sale.Id,
            UserId       = sale.UserId,
            UserFullName = userFullName,
            SaleDate     = sale.SaleDate,
            Total        = sale.Total,
            Details      = details.Select(d =>
            {
                var p = products.First(x => x.Id == d.ProductId);
                return new SaleDetailResponseDto
                {
                    Id          = d.Id,
                    ProductId   = d.ProductId,
                    ProductName = p.Name,
                    ProductSKU  = p.SKU,
                    Quantity    = d.Quantity,
                    UnitPrice   = d.UnitPrice,
                    Subtotal    = d.Subtotal
                };
            }).ToList()
        };

    private static SaleResponseDto MapSaleEntityToDto(Sale sale) => new()
    {
        Id           = sale.Id,
        UserId       = sale.UserId,
        UserFullName = sale.User?.FullName ?? string.Empty,
        SaleDate     = sale.SaleDate,
        Total        = sale.Total,
        Details      = sale.SaleDetails.Select(d => new SaleDetailResponseDto
        {
            Id          = d.Id,
            ProductId   = d.ProductId,
            ProductName = d.Product?.Name ?? string.Empty,
            ProductSKU  = d.Product?.SKU  ?? string.Empty,
            Quantity    = d.Quantity,
            UnitPrice   = d.UnitPrice,
            Subtotal    = d.Subtotal
        }).ToList()
    };
}
