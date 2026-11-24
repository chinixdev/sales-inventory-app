using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Monitoring;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Infrastructure.Monitoring;

/// <summary>
/// Clasificación pura de las instancias del Worker a partir de sus latidos. La antigüedad se calcula con la hora de SQL
/// Server (la misma que escribió el latido), nunca con el reloj de la API.
/// </summary>
public static class WorkerHealthEvaluator
{
    public static WorkerHealthDto Evaluate(IReadOnlyList<WorkerHeartbeat> rows, DateTime serverNowUtc, TimeSpan threshold)
    {
        var thresholdSeconds = (int)threshold.TotalSeconds;
        if (rows.Count == 0)
        {
            // Sin filas nunca hubo un Worker registrado (o su información expiró): no es lo mismo que «inactivo».
            return new WorkerHealthDto(HealthStates.NoInformation, HealthStates.Unknown, thresholdSeconds, []);
        }

        var instances = rows
            .OrderByDescending(row => row.LastSeenAtUtc)
            .Select(row => Classify(row, serverNowUtc, threshold))
            .ToList();

        var active = instances.Where(i => i.Status == HealthStates.Active).ToList();
        var status = active.Count > 0 ? HealthStates.Active : HealthStates.Inactive;

        // Procesamiento global: Healthy si alguna instancia activa procesa bien; Degraded si hay activas pero ninguna lo logra.
        var processing = active.Count == 0 ? HealthStates.Unknown
            : active.Any(i => i.Processing == HealthStates.Healthy) ? HealthStates.Healthy
            : active.Any(i => i.Processing == HealthStates.Degraded) ? HealthStates.Degraded
            : HealthStates.Unknown;

        return new WorkerHealthDto(status, processing, thresholdSeconds, instances);
    }

    public static WorkerInstanceDto Classify(WorkerHeartbeat row, DateTime serverNowUtc, TimeSpan threshold)
    {
        var age = serverNowUtc - row.LastSeenAtUtc;
        var isActive = age <= threshold;

        // Un latido solo prueba que el proceso vive. El procesamiento se juzga por el último ciclo del Outbox.
        string processing;
        if (!isActive)
        {
            processing = HealthStates.Unknown;
        }
        else if (!string.IsNullOrEmpty(row.LastCycleErrorType))
        {
            processing = HealthStates.Degraded;
        }
        else if (row.LastSuccessfulCycleAtUtc is null)
        {
            processing = HealthStates.Unknown; // recién iniciado: aún sin un ciclo completo
        }
        else
        {
            processing = serverNowUtc - row.LastSuccessfulCycleAtUtc.Value > threshold ? HealthStates.Degraded : HealthStates.Healthy;
        }

        return new WorkerInstanceDto(
            row.InstanceId,
            row.Version,
            Utc(row.StartedAtUtc),
            Utc(row.LastSeenAtUtc),
            Math.Max(0, (long)age.TotalSeconds),
            isActive ? HealthStates.Active : HealthStates.Inactive,
            processing,
            row.LastSuccessfulCycleAtUtc is null ? null : Utc(row.LastSuccessfulCycleAtUtc.Value),
            row.LastCycleErrorType);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>
/// Estado operativo agregado. Cada sección se consulta de forma independiente y acotada por <c>QueryTimeout</c>: si SQL
/// Server no responde, la sección queda en <c>null</c>/Unavailable y el estado global es Degraded (nunca ceros ficticios).
/// Solo se devuelven estados estables: sin excepciones, cadenas de conexión, hosts ni direcciones internas.
/// </summary>
public sealed class SystemHealthService(
    SalesInventoryDbContext db,
    IOutboxMonitorService outbox,
    IOptions<SystemHealthOptions> options,
    ILogger<SystemHealthService> logger) : ISystemHealthService
{
    private const int SeriesHours = 24;

    private readonly SystemHealthOptions _options = options.Value;

    private sealed record HourCount(DateTime Date, int Hour, int Count);

    public async Task<SystemHealthDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var api = new ApiHealthDto(HealthStates.Up, AppTelemetry.Version);
        var thresholdSeconds = (int)_options.HeartbeatThreshold.TotalSeconds;

        // 1. SQL Server: la hora del servidor (referencia única) y la latencia. Si falla, todo lo demás depende de él.
        var started = Stopwatch.GetTimestamp();
        var probe = await TryAsync("sql", token => db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS Value").SingleAsync(token), cancellationToken);
        if (!probe.Ok)
        {
            return new SystemHealthDto(
                null,
                HealthStates.Degraded,
                api,
                new SqlServerHealthDto(HealthStates.Unavailable, null),
                new WorkerHealthDto(HealthStates.Unavailable, HealthStates.Unknown, thresholdSeconds, null),
                null,
                HealthStates.Unavailable,
                null,
                ["sql_unavailable"]);
        }

        var serverNow = DateTime.SpecifyKind(probe.Value, DateTimeKind.Utc);
        var sql = new SqlServerHealthDto(HealthStates.Available, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var warnings = new List<string>();

        // 2. Worker (latidos persistidos).
        WorkerHealthDto worker;
        var heartbeats = await TryAsync(
            "worker",
            token => db.WorkerHeartbeats.AsNoTracking().OrderByDescending(h => h.LastSeenAtUtc).Take(50).ToListAsync(token),
            cancellationToken);
        if (heartbeats.Ok)
        {
            worker = WorkerHealthEvaluator.Evaluate(heartbeats.Value!, serverNow, _options.HeartbeatThreshold);
            switch (worker.Status)
            {
                case HealthStates.NoInformation: warnings.Add("worker_no_information"); break;
                case HealthStates.Inactive: warnings.Add("worker_inactive"); break;
            }

            if (worker.Processing == HealthStates.Degraded)
                warnings.Add("worker_processing_degraded");
        }
        else
        {
            worker = new WorkerHealthDto(HealthStates.Unavailable, HealthStates.Unknown, thresholdSeconds, null);
            warnings.Add("worker_unavailable");
        }

        // 3. Eventos: se reutiliza el resumen existente del monitor del Outbox.
        var summary = await TryAsync("events", token => outbox.GetSummaryAsync(token), cancellationToken);

        // 4. Procesados/fallidos por hora (últimas 24 h): agregados en SQL, nunca se cargan filas de eventos.
        var processedRows = await TryAsync("processed-series", token => db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxMessageStatus.Processed && m.ProcessedAtUtc >= DateTime.UtcNow.AddHours(-SeriesHours))
            .GroupBy(m => new { m.ProcessedAtUtc!.Value.Date, m.ProcessedAtUtc.Value.Hour })
            .Select(g => new { g.Key.Date, g.Key.Hour, Count = g.Count() })
            .ToListAsync(token), cancellationToken);
        var failedRows = await TryAsync("failed-series", token => db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxMessageStatus.Failed && m.FailedAtUtc >= DateTime.UtcNow.AddHours(-SeriesHours))
            .GroupBy(m => new { m.FailedAtUtc!.Value.Date, m.FailedAtUtc.Value.Hour })
            .Select(g => new { g.Key.Date, g.Key.Hour, Count = g.Count() })
            .ToListAsync(token), cancellationToken);

        var processed = processedRows.Ok ? processedRows.Value!.Select(r => new HourCount(r.Date, r.Hour, r.Count)).ToList() : null;
        var failed = failedRows.Ok ? failedRows.Value!.Select(r => new HourCount(r.Date, r.Hour, r.Count)).ToList() : null;

        OutboxHealthDto? events = null;
        if (summary.Ok)
        {
            var s = summary.Value!;
            long? oldestAge = s.OldestPendingAtUtc is null
                ? null
                : Math.Max(0, (long)(serverNow - s.OldestPendingAtUtc.Value).TotalSeconds);
            events = new OutboxHealthDto(
                s.Pending, s.Processing, s.Processed, s.Failed, s.ExpiredLeases, s.OldestPendingAtUtc, oldestAge,
                processed?.Sum(p => p.Count),
                failed?.Sum(p => p.Count));

            if (oldestAge is { } age && age > _options.BacklogWarningAfter.TotalSeconds)
                warnings.Add("outbox_backlog_old");
            if (s.ExpiredLeases > 0)
                warnings.Add("outbox_expired_leases");
            if (failed is not null && failed.Sum(p => p.Count) > 0)
                warnings.Add("outbox_failures_recent");
        }
        else
        {
            warnings.Add("events_unavailable");
        }

        // 5. Serie: solo con datos persistidos suficientes (intervalos con actividad).
        string seriesStatus;
        ThroughputSeriesDto? series = null;
        if (processed is null || failed is null)
        {
            seriesStatus = HealthStates.Unavailable;
        }
        else
        {
            series = BuildSeries(processed, failed, serverNow);
            if (series.Points.Count(p => p.Processed + p.Failed > 0) >= _options.SeriesMinimumBuckets)
            {
                seriesStatus = HealthStates.Available;
            }
            else
            {
                seriesStatus = HealthStates.InsufficientData;
                series = null;
            }
        }

        return new SystemHealthDto(
            serverNow,
            warnings.Count == 0 ? HealthStates.Healthy : HealthStates.Degraded,
            api,
            sql,
            worker,
            events,
            seriesStatus,
            series,
            warnings);
    }

    private static ThroughputSeriesDto BuildSeries(IReadOnlyList<HourCount> processed, IReadOnlyList<HourCount> failed, DateTime serverNow)
    {
        static DateTime Bucket(HourCount row) => DateTime.SpecifyKind(row.Date.Date.AddHours(row.Hour), DateTimeKind.Utc);

        var processedByHour = processed.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        var failedByHour = failed.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));

        var last = new DateTime(serverNow.Year, serverNow.Month, serverNow.Day, serverNow.Hour, 0, 0, DateTimeKind.Utc);
        var points = new List<ThroughputPointDto>();
        for (var hour = last.AddHours(-SeriesHours); hour <= last; hour = hour.AddHours(1))
        {
            points.Add(new ThroughputPointDto(
                hour,
                processedByHour.GetValueOrDefault(hour),
                failedByHour.GetValueOrDefault(hour)));
        }

        return new ThroughputSeriesDto(60, points);
    }

    private async Task<(bool Ok, T Value)> TryAsync<T>(string section, Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.QueryTimeout);
        try
        {
            return (true, await query(timeout.Token));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Solo el tipo de excepción: ni el mensaje (puede contener servidor o cadena de conexión) ni la traza.
            logger.LogWarning("Monitoreo: la sección {Section} no está disponible ({ExceptionType}).", section, exception.GetType().Name);
            return (false, default!);
        }
    }
}
