using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Products.DTOs;
using SalesInventory.Application.Products.Interfaces;

namespace SalesInventory.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin, Seller")]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;

    public ProductsController(IProductService productService)
    {
        _productService = productService;
    }

    /// <summary>
    /// Consulta paginada de productos con filtros de búsqueda, categoría y alertas de stock.
    /// </summary>
    /// <param name="filter">Parámetros de paginación y filtrado.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ProductResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ProductResponseDto>>> GetPaged(
        [FromQuery] ProductFilterParams filter,
        CancellationToken cancellationToken)
    {
        var result = await _productService.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Obtiene el detalle de un producto por su identificador.
    /// </summary>
    /// <param name="id">Identificador único del producto.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponseDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _productService.GetByIdAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Crea un nuevo producto y registra transaccionalmente el movimiento de stock inicial (si aplica).
    /// </summary>
    /// <param name="request">Datos del producto y stock inicial.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponseDto>> Create(
        [FromBody] CreateProductRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        request.UserId = userId;
        var result = await _productService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>
    /// Actualiza la información comercial del producto. No permite modificar el stock directamente.
    /// </summary>
    /// <param name="id">Identificador del producto a modificar.</param>
    /// <param name="request">Datos actualizados del producto.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponseDto>> Update(
        int id,
        [FromBody] UpdateProductRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _productService.UpdateAsync(id, request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Elimina o desactiva lógicamente un producto según su historial de movimientos.
    /// </summary>
    /// <param name="id">Identificador del producto a eliminar.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken)
    {
        await _productService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    private bool TryGetAuthenticatedUserId(out int userId)
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(claimValue, out userId) && userId > 0;
    }
}
