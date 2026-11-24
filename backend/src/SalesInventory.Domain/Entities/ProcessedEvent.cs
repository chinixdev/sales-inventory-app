namespace SalesInventory.Domain.Entities;

/// <summary>
/// Registro persistente de que un consumidor ya procesó un evento. La restricción única
/// (EventId, Consumer) hace idempotente al consumidor frente a entregas repetidas (at-least-once).
/// </summary>
public class ProcessedEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }

    /// <summary>Nombre estable del consumidor que procesó el evento.</summary>
    public string Consumer { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }

    /// <summary>Resumen del efecto local aplicado (sin datos sensibles).</summary>
    public string? Summary { get; set; }
}
