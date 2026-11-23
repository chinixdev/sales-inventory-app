using SalesInventory.Domain.Enums;
using SalesInventory.Domain.Exceptions;

namespace SalesInventory.Domain.Entities;

public class StockMovement
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int UserId { get; set; }
    public int? SaleId { get; set; }
    public StockMovementType MovementType { get; set; }
    public int Quantity { get; set; }
    public int PreviousStock { get; set; }
    public int NewStock { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Relaciones de navegación
    public Product? Product { get; set; }
    public User? User { get; set; }
    public Sale? Sale { get; set; }

    public void ValidateBalance()
    {
        if (Quantity <= 0)
            throw new DomainException("La cantidad del movimiento debe ser estrictamente mayor a 0.");

        if (PreviousStock < 0 || NewStock < 0)
            throw new DomainException("El stock previo y nuevo no pueden ser negativos.");

        switch (MovementType)
        {
            case StockMovementType.IN:
                if (NewStock != PreviousStock + Quantity)
                    throw new DomainException("Para movimientos de entrada (IN), NewStock debe ser igual a PreviousStock + Quantity.");
                break;

            case StockMovementType.OUT:
                if (NewStock != PreviousStock - Quantity)
                    throw new DomainException("Para movimientos de salida (OUT), NewStock debe ser igual a PreviousStock - Quantity.");
                break;

            case StockMovementType.ADJUSTMENT:
                if (Math.Abs(NewStock - PreviousStock) != Quantity)
                    throw new DomainException("Para ajustes (ADJUSTMENT), Quantity debe ser el valor absoluto de la diferencia entre NewStock y PreviousStock.");
                break;

            default:
                throw new DomainException("Tipo de movimiento desconocido.");
        }

        if (SaleId.HasValue && MovementType != StockMovementType.OUT)
            throw new DomainException("Un movimiento asociado a una venta solo puede ser de tipo OUT.");
    }
}
