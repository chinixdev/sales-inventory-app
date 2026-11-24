using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Inventory.DTOs;

public class MovementFilterParams
{
    public int? ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
