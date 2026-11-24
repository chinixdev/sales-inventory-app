using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Notifications;

namespace SalesInventory.Api.Controllers;

/// <summary>Notificaciones del usuario autenticado. El usuario sale siempre del JWT; nunca de la solicitud.</summary>
[ApiController]
[Authorize(Roles = "Admin, Seller")]
[Route("api/notifications")]
[Produces("application/json")]
public class NotificationsController(INotificationService notificationService) : ControllerBase
{
    /// <summary>Lista paginada de las notificaciones propias (filtros: unread, type).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<NotificationResponseDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<NotificationResponseDto>>> GetPaged(
        [FromQuery] NotificationFilterParams filter,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(await notificationService.GetPagedAsync(userId, filter, cancellationToken));
    }

    /// <summary>Cantidad de notificaciones propias sin leer.</summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnreadCountDto>> GetUnreadCount(CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(new UnreadCountDto(await notificationService.CountUnreadAsync(userId, cancellationToken)));
    }

    /// <summary>Marca como leída una notificación propia (idempotente). 404 si no existe o es de otro usuario.</summary>
    [HttpPost("{id:long}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsRead(long id, CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        await notificationService.MarkAsReadAsync(userId, id, cancellationToken);
        return NoContent();
    }

    /// <summary>Marca como leídas todas las notificaciones propias.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType(typeof(MarkedReadDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<MarkedReadDto>> MarkAllAsRead(CancellationToken cancellationToken)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(new MarkedReadDto(await notificationService.MarkAllAsReadAsync(userId, cancellationToken)));
    }

    private bool TryGetAuthenticatedUserId(out int userId)
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(claimValue, out userId) && userId > 0;
    }
}
