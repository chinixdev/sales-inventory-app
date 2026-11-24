using Microsoft.EntityFrameworkCore;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<Sale> Sales { get; }
    DbSet<SaleDetail> SaleDetails { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<ProcessedEvent> ProcessedEvents { get; }
    DbSet<Notification> Notifications { get; }

    /// <summary>Auditoría: SOLO LECTURA desde la aplicación (la escribe el propio DbContext al guardar cambios).</summary>
    IQueryable<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Descarta las entidades rastreadas (p. ej. tras un intento de guardado fallido).</summary>
    void ClearChangeTracker();

    /// <summary>
    /// Ejecuta la operación dentro de una única transacción de base de datos y compatible con
    /// <c>EnableRetryOnFailure</c>: ante un fallo transitorio se reintenta la operación completa desde cero.
    /// Si ya existe una transacción externa (p. ej. en pruebas) se reutiliza.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indica si la excepción corresponde a una violación de la restricción única de idempotencia
    /// (misma clave, usuario y operación ya confirmados por otra solicitud).
    /// </summary>
    bool IsDuplicateIdempotencyKey(DbUpdateException exception);
}
