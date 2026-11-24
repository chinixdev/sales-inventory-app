using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Common.Interfaces;

/// <summary>Quién y desde dónde se origina un cambio auditado.</summary>
public sealed record AuditActor(int? UserId, AuditActorType ActorType, AuditSource Source, string CorrelationId);

/// <summary>
/// Proporciona el actor de la operación en curso a partir del contexto autenticado del host (nunca de datos
/// enviados por el cliente). Cada host registra su implementación: API (usuario del JWT) o Worker (System).
/// </summary>
public interface IAuditActorProvider
{
    AuditActor GetCurrent();
}
