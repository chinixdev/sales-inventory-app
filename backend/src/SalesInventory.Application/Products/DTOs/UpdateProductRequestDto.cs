using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Products.DTOs;

public class UpdateProductRequestDto
{
    [Required(ErrorMessage = "La categoría es obligatoria.")]
    [Range(1, int.MaxValue, ErrorMessage = "Debe especificar un identificador de categoría válido.")]
    public int CategoryId { get; set; }

    [Required(ErrorMessage = "El código SKU es obligatorio.")]
    [StringLength(40, MinimumLength = 3, ErrorMessage = "El SKU debe tener entre 3 y 40 caracteres.")]
    public string SKU { get; set; } = string.Empty;

    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [StringLength(150, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 150 caracteres.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "La descripción no puede exceder 500 caracteres.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "El precio es obligatorio.")]
    [Range(0.01, 9999999999999999.99, ErrorMessage = "El precio debe ser estrictamente mayor a 0.")]
    public decimal Price { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "El stock mínimo no puede ser negativo.")]
    public int MinimumStock { get; set; } = 5;

    public bool IsActive { get; set; } = true;
}
