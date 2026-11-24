using Microsoft.EntityFrameworkCore;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Infrastructure.Monitoring;

/// <summary>Identidad de esta instancia del Worker (una por proceso).</summary>
public sealed record WorkerInstanceInfo(Guid InstanceId, string Version)
{
    public static WorkerInstanceInfo Create(string version) => new(Guid.NewGuid(), version);
}

/// <summary>
/// Resultado de los ciclos de sondeo del Outbox, compartido entre el bucle de procesamiento y el servicio de latido. Así el
/// latido informa también si el procesamiento funciona, no solo si el proceso vive.
/// </summary>
public sealed class WorkerCycleTracker
{
    private int _succeededSinceLastBeat;
    private volatile string? _lastErrorType;

    public void RecordSuccess()
    {
        _lastErrorType = null;
        Interlocked.Exchange(ref _succeededSinceLastBeat, 1);
    }

    /// <summary>Devuelve el indicador de éxito (sin tocar el error) cuando un latido no pudo escribirse.</summary>
    public void KeepSuccess() => Interlocked.Exchange(ref _succeededSinceLastBeat, 1);

    /// <summary>Solo el NOMBRE del tipo de excepción: nunca su mensaje.</summary>
    public void RecordFailure(Exception exception) => _lastErrorType = exception.GetType().Name;

    /// <summary>Lo ocurrido desde el último latido (y reinicia el indicador de éxito).</summary>
    public (bool SucceededSinceLastBeat, string? ErrorType) Take() =>
        (Interlocked.Exchange(ref _succeededSinceLastBeat, 0) == 1, _lastErrorType);
}

/// <summary>Escribe el latido en SQL Server con el reloj de SQL Server (SYSUTCDATETIME()), tolerante a varias instancias.</summary>
public sealed class WorkerHeartbeatStore(SalesInventoryDbContext db)
{
    private const int RetentionDays = 7;

    public Task BeatAsync(WorkerInstanceInfo instance, bool cycleSucceeded, string? errorType, CancellationToken cancellationToken)
    {
        var id = instance.InstanceId;
        var version = instance.Version.Length > 50 ? instance.Version[..50] : instance.Version;
        var error = errorType is { Length: > 100 } ? errorType[..100] : errorType;
        var succeeded = cycleSucceeded ? 1 : 0;
        var retentionDays = RetentionDays;

        return db.Database.CreateExecutionStrategy().ExecuteAsync(token => db.Database.ExecuteSqlInterpolatedAsync($@"
            MERGE dbo.WorkerHeartbeats WITH (HOLDLOCK) AS target
            USING (SELECT {id} AS InstanceId) AS source ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                LastSeenAtUtc = SYSUTCDATETIME(),
                Version = {version},
                LastSuccessfulCycleAtUtc = CASE WHEN {succeeded} = 1 THEN SYSUTCDATETIME() ELSE target.LastSuccessfulCycleAtUtc END,
                LastCycleErrorType = {error}
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, StartedAtUtc, LastSeenAtUtc, LastSuccessfulCycleAtUtc, LastCycleErrorType, Version)
                VALUES ({id}, SYSUTCDATETIME(), SYSUTCDATETIME(),
                        CASE WHEN {succeeded} = 1 THEN SYSUTCDATETIME() ELSE NULL END, {error}, {version});

            DELETE FROM dbo.WorkerHeartbeats
            WHERE LastSeenAtUtc < DATEADD(DAY, -{retentionDays}, SYSUTCDATETIME()) AND InstanceId <> {id};", token),
            cancellationToken);
    }
}
