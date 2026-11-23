using Microsoft.AspNetCore.Mvc;

namespace SalesInventory.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;

    public HealthController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "Healthy",
            service = "SalesInventory.Api",
            environment = _environment.EnvironmentName,
            timestampUtc = DateTime.UtcNow,
            version = "1.0.0"
        });
    }
}
