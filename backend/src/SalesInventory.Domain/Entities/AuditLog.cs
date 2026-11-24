namespace SalesInventory.Domain.Entities;

public enum AuditAction
{
    INSERT = 0,
    UPDATE = 1,
    DELETE = 2
}

public enum AuditActorType
{
    /// <summary>Un usuario autenticado (UserId obligatorio).</summary>
    User = 0,

    /// <summary>Un proceso del sistema (worker, arranque, mantenimiento): sin usuario.</summary>
    System = 1
}

public enum AuditSource
{
    API = 0,
    Worker = 1
}

/// <summary>
/// Registro de auditoría de un cambio en una entidad de la lista explícita de auditables. Lo escribe únicamente el
/// DbContext, dentro de la misma transacción que el cambio auditado; la aplicación solo puede leerlo.
/// Nunca contiene contraseñas, hashes, tokens, claves de idempotencia ni otros datos sensibles.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>Nombre de la tabla/entidad auditada (Products, Categories, Sales, SaleDetails, StockMovements, Users).</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Identificador definitivo (ya asignado por SQL Server en los INSERT).</summary>
    public string EntityId { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    /// <summary>Usuario autenticado que originó el cambio (del contexto de seguridad, jamás del cliente); nulo para System.</summary>
    public int? UserId { get; set; }

    public AuditActorType ActorType { get; set; }
    public AuditSource Source { get; set; }

    /// <summary>JSON con los valores anteriores (solo propiedades auditables; solo las modificadas en UPDATE).</summary>
    public string? OldValues { get; set; }

    /// <summary>JSON con los valores nuevos (solo propiedades auditables; solo las modificadas en UPDATE).</summary>
    public string? NewValues { get; set; }

    /// <summary>Nombres de propiedades afectadas, separados por coma.</summary>
    public string ChangedFields { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Identificador que agrupa los cambios de una misma solicitud (API) o unidad de trabajo (Worker).</summary>
    public string CorrelationId { get; set; } = string.Empty;
}
