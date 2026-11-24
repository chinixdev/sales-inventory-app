using System.Diagnostics;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Auditing;

/// <summary>
/// Actor por defecto cuando el host no registró uno: operación del sistema (sin usuario). Los hosts reales lo
/// reemplazan: la API con el usuario autenticado y el Worker con <see cref="WorkerAuditActorProvider"/>.
/// </summary>
public sealed class SystemAuditActorProvider : IAuditActorProvider
{
    private readonly string _correlationId = Guid.NewGuid().ToString("N");

    public AuditActor GetCurrent() => new(null, AuditActorType.System, AuditSource.API, TraceCorrelation.Resolve(_correlationId));
}

/// <summary>
/// Actor del Worker: siempre System/Worker, sin usuario. Se registra con ámbito (scope): cada unidad de trabajo del
/// Worker (un mensaje del Outbox) recibe su propio CorrelationId.
/// </summary>
public sealed class WorkerAuditActorProvider : IAuditActorProvider
{
    private readonly string _correlationId = Guid.NewGuid().ToString("N");

    public AuditActor GetCurrent() => new(null, AuditActorType.System, AuditSource.Worker, TraceCorrelation.Resolve(_correlationId));
}

/// <summary>
/// El CorrelationId de auditoría coincide con el TraceId de la actividad actual cuando existe (así auditoría y trazas se
/// cruzan); sin actividad se conserva el identificador propio de siempre.
/// </summary>
internal static class TraceCorrelation
{
    public static string Resolve(string fallback) =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.TraceId.ToString() : fallback;
}
