using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Categories.DTOs;

public class CreateCategoryRequestDto
{
    [Required(ErrorMessage = "El nombre de la categoría es requerido.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 100 caracteres.")]
    public string Name { get; set; } = string.Empty;
}
