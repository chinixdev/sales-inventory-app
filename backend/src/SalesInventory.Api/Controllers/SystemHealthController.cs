using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Monitoring;

namespace SalesInventory.Api.Controllers;

/// <summary>
/// Centro de monitoreo operativo. Exclusivo de administradores y de solo lectura. Siempre responde 200: si SQL Server no
/// responde, el estado es Degraded y las secciones afectadas vienen en null (no ceros). No expone cadenas de conexión,
/// hosts ni excepciones.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/system-health")]
[Produces("application/json")]
public class SystemHealthController(ISystemHealthService systemHealth) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(SystemHealthDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemHealthDto>> Get(CancellationToken cancellationToken) =>
        Ok(await systemHealth.GetAsync(cancellationToken));
}
