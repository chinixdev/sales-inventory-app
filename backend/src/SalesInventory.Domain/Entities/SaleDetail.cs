using SalesInventory.Domain.Exceptions;

namespace SalesInventory.Domain.Entities;

public class SaleDetail
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }

    // Relaciones de navegación
    public Sale? Sale { get; set; }
    public Product? Product { get; set; }

    public void CalculateSubtotal()
    {
        if (Quantity <= 0)
            throw new DomainException("La cantidad debe ser mayor a 0.");

        if (UnitPrice <= 0)
            throw new DomainException("El precio unitario debe ser mayor a 0.");

        Subtotal = Quantity * UnitPrice;
    }
}
