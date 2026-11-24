using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SalesInventory.Application.Inventory.DTOs;

/// <summary>Registra una entrada manual de stock (tipo IN).</summary>
public class StockEntryRequestDto
{
    [Required(ErrorMessage = "El identificador del producto es obligatorio.")]
    [Range(1, int.MaxValue, ErrorMessage = "El identificador del producto debe ser mayor a 0.")]
    public int ProductId { get; set; }

    [JsonIgnore]
    public int UserId { get; set; }

    [Required(ErrorMessage = "La cantidad es obligatoria.")]
    [Range(1, int.MaxValue, ErrorMessage = "La cantidad debe ser mayor a 0.")]
    public int Quantity { get; set; }

    [Required(ErrorMessage = "El motivo es obligatorio.")]
    [StringLength(300, MinimumLength = 3, ErrorMessage = "El motivo debe tener entre 3 y 300 caracteres.")]
    public string Reason { get; set; } = string.Empty;
}
