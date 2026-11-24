using System.Diagnostics;
using System.Security.Claims;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Api.Security;

/// <summary>
/// Actor de auditoría de la API: el usuario sale EXCLUSIVAMENTE del principal autenticado (claim NameIdentifier del JWT
/// ya validado), nunca de cabeceras, cuerpo ni parámetros enviados por el cliente. Sin sesión autenticada (arranque,
/// seeders) la operación se registra como System.
/// </summary>
public sealed class HttpAuditActorProvider(IHttpContextAccessor accessor) : IAuditActorProvider
{
    public AuditActor GetCurrent()
    {
        var context = accessor.HttpContext;

        // Identificador generado por el servidor para la solicitud (no lo controla el cliente).
        var correlationId = Activity.Current?.TraceId.ToString()
            ?? context?.TraceIdentifier
            ?? Guid.NewGuid().ToString("N");

        var principal = context?.User;
        if (principal?.Identity?.IsAuthenticated == true
            && int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && userId > 0)
        {
            return new AuditActor(userId, AuditActorType.User, AuditSource.API, correlationId);
        }

        return new AuditActor(null, AuditActorType.System, AuditSource.API, correlationId);
    }
}
