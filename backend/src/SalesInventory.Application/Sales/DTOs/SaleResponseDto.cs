namespace SalesInventory.Application.Sales.DTOs;

public class SaleResponseDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string UserFullName { get; set; } = string.Empty;
    public DateTime SaleDate { get; set; }
    public decimal Total { get; set; }
    public List<SaleDetailResponseDto> Details { get; set; } = [];
}

public class SaleDetailResponseDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductSKU { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}
