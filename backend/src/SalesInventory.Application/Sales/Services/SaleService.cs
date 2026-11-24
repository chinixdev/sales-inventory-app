using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Application.Sales.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Domain.Exceptions;

namespace SalesInventory.Application.Sales.Services;

public class SaleService : ISaleService
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<SaleService>? _logger;

    public SaleService(IApplicationDbContext context, ILogger<SaleService>? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    // ─── POST /api/sales ─────────────────────────────────────────────────────

    /// <summary>Resultado ya CONFIRMADO de una solicitud: la venta creada o la original de un replay.</summary>
    private readonly record struct SaleResult(SaleResponseDto Sale, string Outcome, string? ReplayReason = null);

    public async Task<SaleResponseDto> CreateAsync(
        CreateSaleRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = AppTelemetry.Source.StartActivity("sales.create");
        activity?.SetTag("sales.items.count", request.Items?.Count ?? 0);
        var outcome = "failed";
        try
        {
            // Las métricas de éxito se registran UNA vez, aquí, con el resultado ya confirmado: los reintentos
            // internos de la transacción (estrategia de reintentos) ocurren dentro de CreateCoreAsync.
            var result = await CreateCoreAsync(request, cancellationToken);
            outcome = result.Outcome;
            if (result.Outcome == "confirmed")
            {
                AppTelemetry.SalesConfirmed.Add(1);
                _logger?.LogInformation("Venta {SaleId} confirmada con {ItemCount} línea(s).", result.Sale.Id, result.Sale.Details.Count);
            }
            else
            {
                AppTelemetry.IdempotencyReplays.Add(1, new KeyValuePair<string, object?>("reason", result.ReplayReason));
                _logger?.LogInformation("Replay de idempotencia ({Reason}): se devolvió la venta {SaleId} sin cambios.", result.ReplayReason, result.Sale.Id);
            }

            return result.Sale;
        }
        catch (Exception exception)
        {
            outcome = exception switch
            {
                DbUpdateConcurrencyException => "conflict",
                DomainException or ValidationException or NotFoundException or ConflictException => "rejected",
                _ => "failed"
            };

            // Solo un fallo inesperado marca la traza como error; un rechazo de negocio es un resultado normal.
            if (outcome is "failed" or "conflict")
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);

            throw;
        }
        finally
        {
            activity?.SetTag("sales.outcome", outcome);
            AppTelemetry.SaleDuration.Record(
                AppTelemetry.ElapsedSeconds(started),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async Task<SaleResult> CreateCoreAsync(
        CreateSaleRequestDto request,
        CancellationToken cancellationToken)
    {
        // 0. La clave de idempotencia es obligatoria
        if (request.IdempotencyKey == Guid.Empty)
            throw new ValidationException(
                "Se requiere el encabezado Idempotency-Key con un UUID válido para registrar una venta.");

        // 1. Validar usuario
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(User), request.UserId);

        // 2. Validar items: colección no vacía
        if (request.Items == null || request.Items.Count == 0)
            throw new ValidationException("La venta debe contener al menos un producto.");

        // 2.1 Idempotencia: misma clave + mismo contenido => devolver la venta original sin tocar el stock;
        //     misma clave + contenido distinto => conflicto. Se resuelve antes de revalidar stock o productos.
        var requestHash = SaleRequestHash.Compute(request.Items);
        var replay = await TryGetOriginalSaleAsync(request, requestHash, cancellationToken);
        if (replay is not null)
            return new SaleResult(replay, "replayed", "sequential");

        try
        {
            // Una transacción por intento. Con EnableRetryOnFailure, ante un fallo transitorio la estrategia
            // vuelve a ejecutar todo el delegado: se descarta lo rastreado por el intento anterior y se
            // relee el estado (nunca se reutilizan entidades de un intento revertido).
            var attempt = 0;
            var created = await _context.ExecuteInTransactionAsync(async token =>
            {
                if (attempt++ > 0)
                    _context.ClearChangeTracker();

                return await CreateNewSaleAsync(request, user, requestHash, token);
            }, cancellationToken);

            return new SaleResult(created, "confirmed");
        }
        catch (Exception exception) when (MayHaveLostIdempotencyRace(exception))
        {
            // Una solicitud concurrente con la misma clave pudo confirmarse entre la comprobación previa y
            // este intento (el stock ya cambió, RowVersion difiere o saltó el índice único). Se descarta lo
            // rastreado por el intento fallido y se resuelve como reintento; si no hay registro, el error
            // original se conserva tal cual.
            _context.ClearChangeTracker();
            var original = await TryGetOriginalSaleAsync(request, requestHash, cancellationToken);
            if (original is not null)
                return new SaleResult(original, "replayed", "concurrent");

            throw;
        }
    }

    /// <summary>
    /// Solo los fallos que una venta concurrente con la misma clave puede provocar justifican volver a
    /// consultar el registro de idempotencia. Cualquier otro error (FK, timeout, fallos de infraestructura)
    /// se propaga sin reinterpretarse.
    /// </summary>
    private bool MayHaveLostIdempotencyRace(Exception exception) => exception switch
    {
        DomainException or ValidationException or NotFoundException => true,
        DbUpdateConcurrencyException => true,
        DbUpdateException dbUpdate => _context.IsDuplicateIdempotencyKey(dbUpdate),
        _ => false
    };

    private async Task<SaleResponseDto> CreateNewSaleAsync(
        CreateSaleRequestDto request,
        User user,
        string requestHash,
        CancellationToken cancellationToken)
    {
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

        // 7. Guardar venta, detalles, stock, movimientos y registro de idempotencia. Se ejecuta dentro de la
        //    transacción abierta por CreateAsync: nada es definitivo hasta que se confirma al final.
        _context.Sales.Add(sale);
        _context.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId      = request.UserId,
            Operation   = IdempotencyOperations.CreateSale,
            Key         = request.IdempotencyKey,
            RequestHash = requestHash,
            Sale        = sale,
            CreatedAt   = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);

        // 8. Outbox: el evento lleva el SaleId, que solo existe tras el INSERT (identidad de SQL Server), por
        //    eso se registra en un segundo guardado dentro de la MISMA transacción. Si falla, se revierte
        //    también la venta. Un replay de idempotencia nunca llega aquí, así que nunca duplica el evento.
        using (var persist = AppTelemetry.Source.StartActivity("outbox.persist"))
        {
            persist?.SetTag("messaging.event.type", SaleCompletedV1.EventType);
            // El contexto W3C que se guarda en el mensaje es el de esta actividad (hija de la solicitud).
            _context.OutboxMessages.Add(SaleCompletedV1.ToOutboxMessage(sale, details));
            await _context.SaveChangesAsync(cancellationToken);
        }

        return MapToDto(sale, user.FullName, details, products);
    }

    // ─── Idempotencia ────────────────────────────────────────────────────────

    private async Task<SaleResponseDto?> TryGetOriginalSaleAsync(
        CreateSaleRequestDto request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var record = await _context.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.UserId == request.UserId
                     && r.Operation == IdempotencyOperations.CreateSale
                     && r.Key == request.IdempotencyKey,
                cancellationToken);

        if (record is null)
            return null;

        if (!string.Equals(record.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                "La clave de idempotencia ya se utilizó con un contenido distinto. Genere una nueva clave para una venta diferente.");

        return await GetByIdAsync(record.SaleId, cancellationToken);
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
