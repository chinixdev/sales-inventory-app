namespace SalesInventory.Domain.Enums;

/// <summary>Estado de un mensaje del Outbox transaccional.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Listo para procesarse cuando llegue NextAttemptAtUtc.</summary>
    Pending = 0,

    /// <summary>Reservado por un procesador hasta LockedUntilUtc (arrendamiento).</summary>
    Processing = 1,

    /// <summary>Publicado correctamente.</summary>
    Processed = 2,

    /// <summary>Agotó sus reintentos; requiere intervención.</summary>
    Failed = 3
}
