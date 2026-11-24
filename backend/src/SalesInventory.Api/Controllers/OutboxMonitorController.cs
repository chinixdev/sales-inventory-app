using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Outbox;

namespace SalesInventory.Api.Controllers;

/// <summary>Monitor de eventos del Outbox. Exclusivo de administradores; no expone payloads ni excepciones completas.</summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/outbox")]
[Produces("application/json")]
public class OutboxMonitorController(IOutboxMonitorService monitorService) : ControllerBase
{
    /// <summary>Resumen de eventos por estado.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(OutboxSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OutboxSummaryDto>> GetSummary(CancellationToken cancellationToken) =>
        Ok(await monitorService.GetSummaryAsync(cancellationToken));

    /// <summary>Listado paginado con filtros por estado, tipo y rango de fechas (OccurredAtUtc).</summary>
    [HttpGet("messages")]
    [ProducesResponseType(typeof(PagedResult<OutboxMessageListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<OutboxMessageListItemDto>>> GetMessages(
        [FromQuery] OutboxFilterParams filter,
        CancellationToken cancellationToken) =>
        Ok(await monitorService.GetPagedAsync(filter, cancellationToken));

    /// <summary>Detalle técnico seguro de un evento (sin payload).</summary>
    [HttpGet("messages/{id:guid}")]
    [ProducesResponseType(typeof(OutboxMessageDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OutboxMessageDetailDto>> GetMessage(Guid id, CancellationToken cancellationToken) =>
        Ok(await monitorService.GetByIdAsync(id, cancellationToken));

    /// <summary>
    /// Reprocesamiento manual: solo eventos en estado Failed (transición atómica a Pending). 409 si el evento no está
    /// Failed (p. ej. otra solicitud ya lo reencoló), 404 si no existe.
    /// </summary>
    [HttpPost("messages/{id:guid}/requeue")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Requeue(Guid id, CancellationToken cancellationToken)
    {
        await monitorService.RequeueFailedAsync(id, cancellationToken);
        return NoContent();
    }
}
