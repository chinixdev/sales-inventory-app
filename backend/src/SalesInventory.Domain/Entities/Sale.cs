namespace SalesInventory.Domain.Entities;

public class Sale
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTime SaleDate { get; set; } = DateTime.UtcNow;
    public decimal Total { get; set; }

    // Relaciones de navegación
    public User? User { get; set; }
    public ICollection<SaleDetail> SaleDetails { get; set; } = new List<SaleDetail>();
    public ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    public void RecalculateTotal()
    {
        Total = SaleDetails.Sum(d => d.Subtotal);
    }
}
