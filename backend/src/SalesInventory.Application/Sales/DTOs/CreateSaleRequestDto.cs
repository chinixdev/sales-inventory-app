using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SalesInventory.Application.Sales.DTOs;

public class CreateSaleRequestDto
{
    [JsonIgnore]
    public int UserId { get; set; }

    /// <summary>
    /// Clave de idempotencia tomada del encabezado <c>Idempotency-Key</c>; nunca del cuerpo de la solicitud.
    /// </summary>
    [JsonIgnore]
    public Guid IdempotencyKey { get; set; }

    [Required(ErrorMessage = "La venta debe contener al menos un producto.")]
    [MinLength(1, ErrorMessage = "La venta debe contener al menos un producto.")]
    public List<SaleItemRequestDto> Items { get; set; } = [];
}

public class SaleItemRequestDto
{
    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "ProductId debe ser mayor a 0.")]
    public int ProductId { get; set; }

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "La cantidad debe ser mayor a 0.")]
    public int Quantity { get; set; }
}
