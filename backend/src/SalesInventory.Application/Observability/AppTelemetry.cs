using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace SalesInventory.Application.Observability;

/// <summary>
/// Fuente única de trazas y métricas del sistema. Etiquetas SOLO de baja cardinalidad (resultado, tipo de evento,
/// tipo de notificación): nunca UserId, SaleId, EventId ni TraceId. Nada sensible (JWT, claves de idempotencia,
/// payloads, parámetros SQL) viaja en trazas ni métricas.
/// </summary>
public static class AppTelemetry
{
    public const string Name = "SalesInventory";

    public static readonly string Version =
        (typeof(AppTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];

    public static readonly ActivitySource Source = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    // ─── Ventas ───
    /// <summary>Ventas confirmadas (transacción confirmada). Un replay NO cuenta: no crea nada.</summary>
    public static readonly Counter<long> SalesConfirmed =
        Meter.CreateCounter<long>("salesinventory.sales.confirmed", "{sale}", "Ventas confirmadas.");

    public static readonly Counter<long> IdempotencyReplays =
        Meter.CreateCounter<long>("salesinventory.sales.idempotency.replays", "{replay}", "Solicitudes resueltas como replay de idempotencia (reason: sequential|concurrent).");

    public static readonly Histogram<double> SaleDuration =
        Meter.CreateHistogram<double>("salesinventory.sales.duration", "s", "Duración de la creación de ventas (outcome: confirmed|replayed|rejected|conflict|failed).");

    /// <summary>Conflictos de concurrencia devueltos al cliente o por pérdida de lease (source: api|outbox_lease).</summary>
    public static readonly Counter<long> ConcurrencyConflicts =
        Meter.CreateCounter<long>("salesinventory.concurrency.conflicts", "{conflict}", "Conflictos de concurrencia.");

    // ─── Outbox ───
    public static readonly Counter<long> EventsProcessed =
        Meter.CreateCounter<long>("salesinventory.outbox.events.processed", "{event}", "Eventos del Outbox procesados y confirmados.");

    public static readonly Counter<long> EventsFailed =
        Meter.CreateCounter<long>("salesinventory.outbox.events.failed", "{event}", "Eventos que pasaron a Failed (reason: permanent|exhausted|abandoned).");

    public static readonly Counter<long> EventsRetried =
        Meter.CreateCounter<long>("salesinventory.outbox.events.retried", "{event}", "Eventos devueltos a Pending para reintentarse.");

    public static readonly Histogram<double> EventDuration =
        Meter.CreateHistogram<double>("salesinventory.outbox.event.duration", "s", "Duración del procesamiento de un evento (outcome: processed|retried|failed|lease_lost).");

    public static readonly Counter<long> NotificationsGenerated =
        Meter.CreateCounter<long>("salesinventory.notifications.generated", "{notification}", "Notificaciones confirmadas (type: LowStock|OutOfStock).");

    public static double ElapsedSeconds(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
}
