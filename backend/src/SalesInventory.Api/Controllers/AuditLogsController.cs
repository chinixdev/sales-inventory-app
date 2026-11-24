using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Audit;
using SalesInventory.Application.Common.Models;

namespace SalesInventory.Api.Controllers;

/// <summary>
/// Consulta de auditoría. Exclusivo de administradores y de SOLO LECTURA: no existen endpoints para crear, editar ni
/// eliminar registros (la aplicación no puede modificarlos; esto no sustituye los permisos de la base de datos).
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/audit-logs")]
[Produces("application/json")]
public class AuditLogsController(IAuditLogService auditLogService) : ControllerBase
{
    /// <summary>Listado paginado (más recientes primero) con filtros por entidad, acción, usuario, fecha y correlación.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AuditLogListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AuditLogListItemDto>>> GetPaged(
        [FromQuery] AuditLogFilterParams filter,
        CancellationToken cancellationToken) =>
        Ok(await auditLogService.GetPagedAsync(filter, cancellationToken));

    /// <summary>Detalle de un registro con valores anteriores y nuevos (solo propiedades auditables).</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(AuditLogDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuditLogDetailDto>> GetById(long id, CancellationToken cancellationToken) =>
        Ok(await auditLogService.GetByIdAsync(id, cancellationToken));
}
