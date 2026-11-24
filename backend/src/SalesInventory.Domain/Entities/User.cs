using SalesInventory.Domain.Enums;

namespace SalesInventory.Domain.Entities;

public class User
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Seller;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Relaciones de navegación
    public ICollection<Sale> Sales { get; set; } = new List<Sale>();
    public ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();
}
