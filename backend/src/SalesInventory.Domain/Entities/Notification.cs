namespace SalesInventory.Domain.Entities;

public enum NotificationType
{
    /// <summary>El stock actual es menor o igual al mínimo configurado, pero hay existencias.</summary>
    LowStock = 0,

    /// <summary>El producto se quedó sin existencias.</summary>
    OutOfStock = 1
}

/// <summary>Notificación persistente dirigida a un usuario concreto (aislada por <see cref="UserId"/>).</summary>
public class Notification
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public NotificationType Type { get; set; }
    public int ProductId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>Stock del producto al evaluar el evento (instantánea; la alerta se calcula con el stock vigente).</summary>
    public int CurrentStock { get; set; }

    /// <summary>Umbral (stock mínimo) configurado en el producto al evaluar el evento.</summary>
    public int MinimumStock { get; set; }

    /// <summary>Evento de Outbox que originó la notificación (trazabilidad e idempotencia).</summary>
    public Guid SourceEventId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    public User? User { get; set; }
    public Product? Product { get; set; }
}
