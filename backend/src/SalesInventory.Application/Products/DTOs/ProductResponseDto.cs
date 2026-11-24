namespace SalesInventory.Application.Products.DTOs;

public class ProductResponseDto
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public int MinimumStock { get; set; }
    public bool IsActive { get; set; }
    public bool IsBelowMinimumStock { get; set; }
    public DateTime CreatedAt { get; set; }
}
