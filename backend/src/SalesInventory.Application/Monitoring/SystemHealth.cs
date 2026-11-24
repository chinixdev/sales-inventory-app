namespace SalesInventory.Application.Monitoring;

/// <summary>Estados de componente: valores estables y sin detalles internos (nunca excepciones, hosts ni cadenas de conexión).</summary>
public static class HealthStates
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";

    public const string Up = "Up";
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";

    public const string Active = "Active";
    public const string Inactive = "Inactive";
    public const string NoInformation = "NoInformation";
    public const string Unknown = "Unknown";

    public const string InsufficientData = "InsufficientData";
}

public sealed record ApiHealthDto(string Status, string Version);

public sealed record SqlServerHealthDto(string Status, long? LatencyMs);

/// <summary>
/// <c>Status</c> indica si el proceso late (Active/Inactive); <c>Processing</c> indica si el procesamiento parece funcionar
/// (Healthy/Degraded/Unknown). Un latido NO garantiza el procesamiento de eventos.
/// </summary>
public sealed record WorkerInstanceDto(
    Guid InstanceId,
    string Version,
    DateTime StartedAtUtc,
    DateTime LastSeenAtUtc,
    long SecondsSinceLastSeen,
    string Status,
    string Processing,
    DateTime? LastSuccessfulCycleAtUtc,
    string? LastErrorType);

/// <summary>Status: Active | Inactive | NoInformation | Unavailable (SQL Server no respondió: sin datos, no «sin Worker»).</summary>
public sealed record WorkerHealthDto(
    string Status,
    string Processing,
    int HeartbeatThresholdSeconds,
    IReadOnlyList<WorkerInstanceDto>? Instances);

/// <summary>Los totales de 24 h son null (no 0) cuando su consulta no estuvo disponible.</summary>
public sealed record OutboxHealthDto(
    int Pending,
    int Processing,
    int Processed,
    int Failed,
    int ExpiredLeases,
    DateTime? OldestPendingAtUtc,
    long? OldestPendingAgeSeconds,
    int? ProcessedLast24Hours,
    int? FailedLast24Hours);

public sealed record ThroughputPointDto(DateTime BucketStartUtc, int Processed, int Failed);

/// <summary>Serie horaria agregada a partir de marcas persistidas. Solo se incluye con datos suficientes.</summary>
public sealed record ThroughputSeriesDto(int BucketMinutes, IReadOnlyList<ThroughputPointDto> Points);

public sealed record SystemHealthDto(
    DateTime? GeneratedAtUtc,
    string Status,
    ApiHealthDto Api,
    SqlServerHealthDto SqlServer,
    WorkerHealthDto Worker,
    OutboxHealthDto? Events,
    string SeriesStatus,
    ThroughputSeriesDto? Series,
    IReadOnlyList<string> Warnings);

public interface ISystemHealthService
{
    /// <summary>
    /// Estado operativo agregado. Nunca lanza por fallos de SQL Server: devuelve Degraded con las secciones no disponibles
    /// en <c>null</c> (jamás ceros ficticios).
    /// </summary>
    Task<SystemHealthDto> GetAsync(CancellationToken cancellationToken = default);
}
