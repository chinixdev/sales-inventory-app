using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Inventory.DTOs;
using SalesInventory.Application.Inventory.Interfaces;

namespace SalesInventory.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin, Seller")]
[Route("api/inventory")]
[Produces("application/json")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventoryService;

    public InventoryController(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    /// <summary>
    /// Lista paginada de movimientos de inventario, opcionalmente filtrada por producto.
    /// </summary>
    /// <param name="filter">Parámetros de paginación y filtro opcional por productId.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpGet("movements")]
    [ProducesResponseType(typeof(PagedResult<StockMovementResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StockMovementResponseDto>>> GetMovements(
        [FromQuery] MovementFilterParams filter,
        CancellationToken cancellationToken)
    {
        var result = await _inventoryService.GetMovementsAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Registra una entrada manual de stock (IN). Actualiza Products.Stock y crea StockMovement en una sola transacción.
    /// </summary>
    /// <param name="request">ProductId, Quantity y Reason.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPost("entries")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(StockMovementResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockMovementResponseDto>> RegisterEntry(
        [FromBody] StockEntryRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        request.UserId = userId;
        var result = await _inventoryService.RegisterEntryAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetMovements), new { productId = result.ProductId }, result);
    }

    /// <summary>
    /// Ajusta el stock de un producto a una cantidad final concreta (ADJUSTMENT).
    /// Calcula el delta automáticamente. No permite stock negativo.
    /// </summary>
    /// <param name="request">ProductId, NewStock y Reason.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    [HttpPost("adjustments")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(StockMovementResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockMovementResponseDto>> RegisterAdjustment(
        [FromBody] StockAdjustmentRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        request.UserId = userId;
        var result = await _inventoryService.RegisterAdjustmentAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetMovements), new { productId = result.ProductId }, result);
    }

    private bool TryGetAuthenticatedUserId(out int userId)
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(claimValue, out userId) && userId > 0;
    }
}
