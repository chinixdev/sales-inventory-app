using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Auditing;
using SalesInventory.Infrastructure.Persistence.Configurations;

namespace SalesInventory.Infrastructure.Persistence;

public class SalesInventoryDbContext : DbContext, IApplicationDbContext
{
    private readonly IAuditActorProvider? _auditActorProvider;
    private bool _writingAudit;

    public SalesInventoryDbContext(DbContextOptions<SalesInventoryDbContext> options)
        : this(options, null)
    {
    }

    public SalesInventoryDbContext(DbContextOptions<SalesInventoryDbContext> options, IAuditActorProvider? auditActorProvider)
        : base(options)
    {
        _auditActorProvider = auditActorProvider;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleDetail> SaleDetails => Set<SaleDetail>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();

    /// <summary>Vista de solo lectura para la capa de aplicación.</summary>
    IQueryable<AuditLog> IApplicationDbContext.AuditLogs => AuditLogs.AsNoTracking();

    public void ClearChangeTracker() => ChangeTracker.Clear();

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // Los proveedores no relacionales (InMemory) no tienen transacciones. Con una transacción ya abierta
        // (UseTransaction) la estrategia con reintentos no puede envolverla, así que se reutiliza tal cual.
        if (!Database.IsRelational() || Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await Database.BeginTransactionAsync(token);
            var result = await operation(token);
            await transaction.CommitAsync(token);
            return result;
        }, cancellationToken);
    }

    /// <summary>SQL Server: 2601 (índice único) o 2627 (restricción única) sobre el índice de idempotencia.</summary>
    public bool IsDuplicateIdempotencyKey(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 } sqlException
                && sqlException.Message.Contains(
                    IdempotencyRecordConfiguration.UniqueIndexName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ─── Auditoría transversal ───────────────────────────────────────────────
    //
    // Mecanismo: se sobrescribe SaveChanges en el propio DbContext en lugar de usar un SaveChangesInterceptor porque la
    // auditoría debe ser ATÓMICA con el cambio y compatible con EnableRetryOnFailure:
    //  · Un interceptor no puede envolver el guardado en una transacción gestionada por la estrategia de reintentos
    //    (una transacción iniciada fuera de ella es rechazada), ni sabe si se ejecuta dentro o fuera de ella.
    //  · Los Id generados por SQL Server de los INSERT solo existen DESPUÉS de guardar, y el registro de auditoría debe
    //    llevar el Id definitivo; guardarlo en un SavedChanges separado dejaría una ventana sin atomicidad.
    // Aquí: se capturan los cambios auditables antes de guardar, se guarda el negocio y se insertan las filas de
    // AuditLogs (SQL directo, sin pasar por el rastreador: no hay recursión) en la MISMA transacción.

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_writingAudit)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        ChangeTracker.DetectChanges();
        EnsureAuditLogsAreReadOnly();
        var capture = AuditCapture.From(ChangeTracker);
        if (capture.IsEmpty)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        if (!Database.IsRelational())
        {
            // Proveedores sin transacciones (InMemory, solo pruebas unitarias).
            var saved = await SaveBusinessAsync(acceptAllChangesOnSuccess, cancellationToken);
            await AddAuditEntitiesAsync(capture, cancellationToken);
            return saved;
        }

        if (Database.CurrentTransaction is not null)
        {
            // Una transacción externa (p. ej. ExecuteInTransactionAsync) abarca negocio y auditoría; ella decide el commit.
            var saved = await SaveBusinessAsync(acceptAllChangesOnSuccess, cancellationToken);
            await InsertAuditRowsAsync(capture, cancellationToken);
            return saved;
        }

        // Sin transacción previa: negocio + auditoría en una transacción gestionada por la estrategia de reintentos.
        // Se guarda con acceptAllChangesOnSuccess = false y se aceptan los cambios solo tras el commit, de modo que un
        // reintento de la estrategia vuelva a ver los cambios pendientes.
        var strategy = Database.CreateExecutionStrategy();
        var attempt = 0;
        var result = await strategy.ExecuteAsync(async token =>
        {
            if (attempt++ > 0)
            {
                capture.ResetGeneratedKeys();
            }

            await using var transaction = await Database.BeginTransactionAsync(token);
            var count = await SaveBusinessAsync(false, token);
            await InsertAuditRowsAsync(capture, token);
            await transaction.CommitAsync(token);
            return count;
        }, cancellationToken);

        if (acceptAllChangesOnSuccess)
        {
            ChangeTracker.AcceptAllChanges();
        }

        return result;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_writingAudit)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        ChangeTracker.DetectChanges();
        EnsureAuditLogsAreReadOnly();
        var capture = AuditCapture.From(ChangeTracker);
        if (capture.IsEmpty)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        if (!Database.IsRelational())
        {
            var saved = SaveBusiness(acceptAllChangesOnSuccess);
            AddAuditEntities(capture);
            return saved;
        }

        if (Database.CurrentTransaction is not null)
        {
            var saved = SaveBusiness(acceptAllChangesOnSuccess);
            InsertAuditRows(capture);
            return saved;
        }

        var strategy = Database.CreateExecutionStrategy();
        var attempt = 0;
        var result = strategy.Execute(() =>
        {
            if (attempt++ > 0)
            {
                capture.ResetGeneratedKeys();
            }

            using var transaction = Database.BeginTransaction();
            var count = SaveBusiness(false);
            InsertAuditRows(capture);
            transaction.Commit();
            return count;
        });

        if (acceptAllChangesOnSuccess)
        {
            ChangeTracker.AcceptAllChanges();
        }

        return result;
    }

    // `base` no puede usarse dentro de lambdas: estos envoltorios llaman al guardado real de EF.
    private Task<int> SaveBusinessAsync(bool acceptAll, CancellationToken cancellationToken) =>
        base.SaveChangesAsync(acceptAll, cancellationToken);

    private int SaveBusiness(bool acceptAll) => base.SaveChanges(acceptAll);

    private AuditActor CurrentActor() =>
        _auditActorProvider?.GetCurrent()
        ?? new AuditActor(null, AuditActorType.System, AuditSource.API, Guid.NewGuid().ToString("N"));

    private async Task InsertAuditRowsAsync(AuditCapture capture, CancellationToken cancellationToken)
    {
        foreach (var log in capture.Build(CurrentActor(), DateTime.UtcNow))
        {
            await Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dbo.AuditLogs
                    (EntityName, EntityId, [Action], UserId, ActorType, [Source], OldValues, NewValues, ChangedFields, OccurredAtUtc, CorrelationId)
                VALUES
                    ({log.EntityName}, {log.EntityId}, {log.Action.ToString()}, {log.UserId}, {log.ActorType.ToString()},
                     {log.Source.ToString()}, {log.OldValues}, {log.NewValues}, {log.ChangedFields}, {log.OccurredAtUtc}, {log.CorrelationId})",
                cancellationToken);
        }
    }

    private void InsertAuditRows(AuditCapture capture)
    {
        foreach (var log in capture.Build(CurrentActor(), DateTime.UtcNow))
        {
            Database.ExecuteSqlInterpolated($@"
                INSERT INTO dbo.AuditLogs
                    (EntityName, EntityId, [Action], UserId, ActorType, [Source], OldValues, NewValues, ChangedFields, OccurredAtUtc, CorrelationId)
                VALUES
                    ({log.EntityName}, {log.EntityId}, {log.Action.ToString()}, {log.UserId}, {log.ActorType.ToString()},
                     {log.Source.ToString()}, {log.OldValues}, {log.NewValues}, {log.ChangedFields}, {log.OccurredAtUtc}, {log.CorrelationId})");
        }
    }

    private async Task AddAuditEntitiesAsync(AuditCapture capture, CancellationToken cancellationToken)
    {
        _writingAudit = true;
        try
        {
            AuditLogs.AddRange(capture.Build(CurrentActor(), DateTime.UtcNow));
            await base.SaveChangesAsync(true, cancellationToken);
        }
        finally
        {
            _writingAudit = false;
        }
    }

    private void AddAuditEntities(AuditCapture capture)
    {
        _writingAudit = true;
        try
        {
            AuditLogs.AddRange(capture.Build(CurrentActor(), DateTime.UtcNow));
            base.SaveChanges(true);
        }
        finally
        {
            _writingAudit = false;
        }
    }

    /// <summary>Los registros de auditoría son de solo lectura desde la aplicación: crearlos, editarlos o borrarlos falla.</summary>
    private void EnsureAuditLogsAreReadOnly()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Los registros de auditoría son de solo lectura: solo el DbContext los escribe al guardar cambios auditables.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
