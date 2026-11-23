using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Interfaces;

namespace SalesInventory.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin, Seller")]
[Route("api/[controller]")]
[Produces("application/json")]
public class SalesController : ControllerBase
{
    private readonly ISaleService _saleService;

    public SalesController(ISaleService saleService)
    {
        _saleService = saleService;
    }

    /// <summary>
    /// Lista paginada de ventas. Filtros opcionales: userId, from, to.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<SaleResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<SaleResponseDto>>> GetPaged(
        [FromQuery] SaleFilterParams filter,
        CancellationToken cancellationToken)
    {
        var result = await _saleService.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Obtiene una venta con todos sus detalles por Id.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(SaleResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SaleResponseDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _saleService.GetByIdAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Registra una venta completa con múltiples productos.
    /// Descuenta stock, crea StockMovements tipo OUT y calcula totales en backend.
    /// Toda la operación se ejecuta en una única transacción con rollback automático ante error.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin, Seller")]
    [ProducesResponseType(typeof(SaleResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SaleResponseDto>> Create(
        [FromBody] CreateSaleRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        request.UserId = userId;
        var result = await _saleService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    private bool TryGetAuthenticatedUserId(out int userId)
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(claimValue, out userId) && userId > 0;
    }
}
