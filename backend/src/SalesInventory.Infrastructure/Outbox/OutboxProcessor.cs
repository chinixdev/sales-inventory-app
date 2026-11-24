using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Outbox;

namespace SalesInventory.Infrastructure.Outbox;

/// <summary>
/// Procesa un lote del Outbox: reclama mensajes de forma atómica y los procesa uno a uno, cada uno en su propio
/// scope de DI y DbContext. Pueden ejecutarse varias instancias en paralelo (otros procesos o máquinas): la
/// exclusión mutua la garantiza SQL Server (lease + RowVersion), no la memoria del proceso.
/// </summary>
public sealed class OutboxProcessor
{
    private static readonly TimeSpan BookkeepingTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxOptions _options;
    private readonly OutboxBackoffCalculator _backoff;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IServiceScopeFactory scopes,
        IOptions<OutboxOptions> options,
        OutboxBackoffCalculator backoff,
        TimeProvider clock,
        ILogger<OutboxProcessor> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _options.Validate();
        _backoff = backoff;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Reclama y procesa un lote. Devuelve cuántos mensajes se reclamaron.</summary>
    /// <param name="stoppingToken">
    /// Solo detiene el reclamo y el inicio de nuevos mensajes. Un mensaje en curso termina (acotado por
    /// HandlerTimeout) y los reclamados sin empezar se devuelven a Pending: apagado ordenado.
    /// </param>
    public async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<ClaimedOutboxMessage> claimed;
        using (var claimActivity = AppTelemetry.Source.StartActivity("outbox.claim"))
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();

            var abandoned = await store.FailExhaustedAbandonedAsync(stoppingToken);
            if (abandoned > 0)
            {
                _logger.LogError(
                    "{Count} mensaje(s) del Outbox pasaron a Failed: se abandonaron y agotaron sus intentos.", abandoned);
                AppTelemetry.EventsFailed.Add(
                    abandoned,
                    new KeyValuePair<string, object?>("event_type", "unknown"),
                    new KeyValuePair<string, object?>("reason", "abandoned"));
            }

            claimed = await store.ClaimBatchAsync(Guid.NewGuid(), stoppingToken);
            claimActivity?.SetTag("outbox.messages.claimed", claimed.Count);
        }

        foreach (var message in claimed)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                await ReleaseAsync(message);
                continue;
            }

            await ProcessOneAsync(message);
        }

        return claimed.Count;
    }

    private async Task ProcessOneAsync(ClaimedOutboxMessage message)
    {
        using var timeout = new CancellationTokenSource(_options.HandlerTimeout, _clock);
        var started = Stopwatch.GetTimestamp();

        // Cada procesamiento es su PROPIA traza (raíz) enlazada con la operación original mediante un ActivityLink;
        // los mensajes anteriores a esta capacidad no tienen contexto y simplemente no llevan enlace.
        IEnumerable<ActivityLink>? links = TraceContextPropagation.TryParse(message.TraceParent, message.TraceState, out var origin)
            ? new[] { new ActivityLink(origin) }
            : null;
        using var activity = AppTelemetry.Source.StartActivity(
            "outbox.process", ActivityKind.Consumer, parentContext: default, tags: null, links: links);
        activity?.SetTag("messaging.event.type", message.EventType);
        activity?.SetTag("outbox.retry_count", message.RetryCount);

        var outcome = "failed";
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
            var deferred = scope.ServiceProvider.GetService<DeferredMetrics>();
            var envelope = new OutboxEnvelope(message.Id, message.EventType, message.Payload, message.OccurredAtUtc);

            await store.CompleteAsync(
                message,
                token =>
                {
                    // La estrategia puede repetir el efecto completo: lo acumulado por un intento anterior se descarta.
                    deferred?.Reset();
                    return dispatcher.DispatchAsync(envelope, token);
                },
                timeout.Token);

            // Solo tras el commit: nada se contabiliza por intentos internos revertidos.
            deferred?.Flush();
            outcome = "processed";
            AppTelemetry.EventsProcessed.Add(1, new KeyValuePair<string, object?>("event_type", message.EventType));
            _logger.LogInformation("Mensaje {MessageId} ({EventType}) procesado.", message.Id, message.EventType);
        }
        catch (LeaseLostException)
        {
            outcome = "lease_lost";
            AppTelemetry.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("source", "outbox_lease"));
            _logger.LogWarning(
                "Mensaje {MessageId}: el lease se perdió; otro worker lo reclamó. Se descarta este intento.", message.Id);
        }
        catch (PermanentOutboxException exception)
        {
            outcome = await FailAsync(message, OutboxErrorFormatter.Format(exception), permanent: true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            outcome = await FailAsync(
                message,
                $"Tiempo límite excedido ({_options.HandlerTimeout.TotalSeconds:0.#} s).",
                permanent: false);
        }
        catch (Exception exception)
        {
            outcome = await FailAsync(message, OutboxErrorFormatter.Format(exception), permanent: false);
        }
        finally
        {
            activity?.SetTag("outbox.outcome", outcome);
            if (outcome is "failed" or "retried")
                activity?.SetStatus(ActivityStatusCode.Error);

            AppTelemetry.EventDuration.Record(
                AppTelemetry.ElapsedSeconds(started),
                new KeyValuePair<string, object?>("event_type", message.EventType),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Registra el fallo y devuelve el resultado (retried | failed | lease_lost) para métricas y traza.</summary>
    private async Task<string> FailAsync(ClaimedOutboxMessage message, string error, bool permanent)
    {
        try
        {
            using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
            var delay = _backoff.Next(message.RetryCount + 1);

            var recorded = await store.RecordFailureAsync(message, error, permanent, delay, bookkeeping.Token);
            if (!recorded)
            {
                _logger.LogWarning(
                    "Mensaje {MessageId}: no se registró el fallo porque el lease ya no es propio.", message.Id);
                AppTelemetry.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("source", "outbox_lease"));
                return "lease_lost";
            }

            var eventType = new KeyValuePair<string, object?>("event_type", message.EventType);
            if (permanent || message.RetryCount + 1 >= _options.MaxAttempts)
            {
                AppTelemetry.EventsFailed.Add(
                    1, eventType, new KeyValuePair<string, object?>("reason", permanent ? "permanent" : "exhausted"));
                _logger.LogError(
                    "Mensaje {MessageId} ({EventType}) pasó a Failed tras {Attempts} intento(s): {Error}",
                    message.Id, message.EventType, message.RetryCount + 1, error);
                return "failed";
            }
            else
            {
                AppTelemetry.EventsRetried.Add(1, eventType);
                _logger.LogWarning(
                    "Mensaje {MessageId} ({EventType}) falló (intento {Attempt}/{Max}); se reintentará en {Delay:0.#} s: {Error}",
                    message.Id, message.EventType, message.RetryCount + 1, _options.MaxAttempts, delay.TotalSeconds, error);
                return "retried";
            }
        }
        catch (Exception exception)
        {
            // Si ni siquiera se pudo registrar el fallo (p. ej. SQL Server caído), el mensaje queda Processing y se
            // recupera al vencer su lease, contabilizado como intento abandonado.
            _logger.LogError(exception, "Mensaje {MessageId}: no se pudo registrar el fallo.", message.Id);
            return "failed";
        }
    }

    private async Task ReleaseAsync(ClaimedOutboxMessage message)
    {
        try
        {
            using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutboxStore>().ReleaseAsync(message, bookkeeping.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Mensaje {MessageId}: no se pudo liberar; se recuperará al vencer el lease.", message.Id);
        }
    }

}
