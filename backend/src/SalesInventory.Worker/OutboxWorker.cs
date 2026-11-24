using Microsoft.Extensions.Options;
using SalesInventory.Infrastructure.Monitoring;
using SalesInventory.Infrastructure.Outbox;

namespace SalesInventory.Worker;

/// <summary>Sondea el Outbox y delega el procesamiento en <see cref="OutboxProcessor"/> hasta que el host se detiene.</summary>
public sealed class OutboxWorker(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorker> logger,
    WorkerCycleTracker tracker) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        settings.Validate(); // falla al arrancar si la configuración es inválida

        logger.LogInformation(
            "OutboxWorker iniciado (lote {Batch}, sondeo {Polling}, lease {Lease}, intentos máx. {Max}).",
            settings.BatchSize, settings.PollingInterval, settings.LeaseDuration, settings.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await processor.ProcessBatchAsync(stoppingToken);
                tracker.RecordSuccess(); // un ciclo sin error (aunque no hubiera trabajo): el procesamiento funciona
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Un fallo del ciclo (p. ej. SQL Server no disponible) no debe tumbar el proceso: se reintenta.
                logger.LogError(exception, "Error al procesar el lote del Outbox; se reintentará.");
                tracker.RecordFailure(exception);
            }

            // Con el lote lleno probablemente hay más trabajo: no se espera.
            if (claimed >= settings.BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(settings.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("OutboxWorker detenido.");
    }
}
