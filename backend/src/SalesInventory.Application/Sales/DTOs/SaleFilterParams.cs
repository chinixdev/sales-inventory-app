using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Sales.DTOs;

public class SaleFilterParams
{
    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    public int? UserId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}
