using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Categories.DTOs;
using SalesInventory.Application.Categories.Interfaces;

namespace SalesInventory.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin, Seller")]
[Route("api/[controller]")]
[Produces("application/json")]
public class CategoriesController : ControllerBase
{
    private readonly ICategoryService _categoryService;

    public CategoriesController(ICategoryService categoryService)
    {
        _categoryService = categoryService;
    }

    /// <summary>
    /// Obtiene la lista completa de categorías.
    /// </summary>
    /// <param name="activeOnly">Opcional. Filtrar solo categorías activas (true/false).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CategoryResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponseDto>>> GetAll(
        [FromQuery] bool? activeOnly,
        CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetAllAsync(activeOnly, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Obtiene una categoría por su identificador único.
    /// </summary>
    /// <param name="id">Identificador único de la categoría.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryResponseDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetByIdAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Crea una nueva categoría en el sistema.
    /// </summary>
    /// <param name="request">Datos de la categoría a crear.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponseDto>> Create(
        [FromBody] CreateCategoryRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _categoryService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>
    /// Actualiza una categoría existente.
    /// </summary>
    /// <param name="id">Identificador de la categoría a actualizar.</param>
    /// <param name="request">Datos actualizados.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(CategoryResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponseDto>> Update(
        int id,
        [FromBody] UpdateCategoryRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _categoryService.UpdateAsync(id, request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Elimina una categoría si no contiene productos asociados.
    /// </summary>
    /// <param name="id">Identificador de la categoría a eliminar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken)
    {
        await _categoryService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
