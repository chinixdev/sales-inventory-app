using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SalesInventory.Application.Inventory.DTOs;

/// <summary>Ajusta el stock de un producto a una cantidad final concreta (tipo ADJUSTMENT).</summary>
public class StockAdjustmentRequestDto
{
    [Required(ErrorMessage = "El identificador del producto es obligatorio.")]
    [Range(1, int.MaxValue, ErrorMessage = "El identificador del producto debe ser mayor a 0.")]
    public int ProductId { get; set; }

    [JsonIgnore]
    public int UserId { get; set; }

    /// <summary>Cantidad final de stock deseada (no el delta).</summary>
    [Range(0, int.MaxValue, ErrorMessage = "El stock final no puede ser negativo.")]
    public int NewStock { get; set; }

    [Required(ErrorMessage = "El motivo es obligatorio.")]
    [StringLength(300, MinimumLength = 3, ErrorMessage = "El motivo debe tener entre 3 y 300 caracteres.")]
    public string Reason { get; set; } = string.Empty;
}
