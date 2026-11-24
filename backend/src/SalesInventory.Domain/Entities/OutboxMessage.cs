using SalesInventory.Domain.Enums;

namespace SalesInventory.Domain.Entities;

/// <summary>
/// Evento de negocio persistido en la misma transacción que el cambio que lo origina (Transactional Outbox).
/// El procesamiento (publicación, reintentos, bloqueos) lo realizará un worker posterior.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Tipo versionado del evento, p. ej. <c>SaleCompleted.v1</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>JSON del contrato explícito del evento (nunca una entidad EF serializada).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }

    /// <summary>Instante (reloj de SQL Server) en que el mensaje pasó a Failed. Nulo en mensajes Failed anteriores a esta columna.</summary>
    public DateTime? FailedAtUtc { get; set; }
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;
    public int RetryCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>
    /// Contexto W3C (traceparent) de la operación que originó el evento. Nulo en eventos anteriores o sin traza activa.
    /// Solo sirve para enlazar el procesamiento asíncrono con la operación original; no es un dato de negocio.
    /// </summary>
    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }

    public static OutboxMessage CreatePending(
        Guid id,
        string eventType,
        string payload,
        DateTime occurredAtUtc,
        string? traceParent = null,
        string? traceState = null) => new()
    {
        TraceParent = traceParent,
        TraceState = traceState,
        Id = id,
        EventType = eventType,
        Payload = payload,
        OccurredAtUtc = occurredAtUtc,
        Status = OutboxMessageStatus.Pending,
        RetryCount = 0,
        NextAttemptAtUtc = occurredAtUtc
    };
}
