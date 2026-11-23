using SalesInventory.Domain.Exceptions;

namespace SalesInventory.Domain.Entities;

public class Product
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public int MinimumStock { get; set; } = 5;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Token de concurrencia optimista (ROWVERSION en SQL Server)
    public byte[] RowVersion { get; set; } = [];

    // Propiedad de conveniencia de dominio
    public bool IsBelowMinimumStock => Stock <= MinimumStock;

    // Relaciones de navegación
    public Category? Category { get; set; }
    public ICollection<SaleDetail> SaleDetails { get; set; } = new List<SaleDetail>();
    public ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    // Métodos de comportamiento de dominio para control de inventario
    public void DeductStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad a descontar debe ser mayor a cero.");

        if (Stock < quantity)
            throw new InsufficientStockException(Id, quantity, Stock);

        Stock -= quantity;
    }

    public void AddStock(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad a ingresar debe ser mayor a cero.");

        Stock += quantity;
    }

    public void AdjustStock(int newStock)
    {
        if (newStock < 0)
            throw new DomainException("El nuevo stock no puede ser negativo.");

        Stock = newStock;
    }
}
