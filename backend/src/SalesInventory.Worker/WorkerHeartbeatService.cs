using Microsoft.Extensions.Options;
using SalesInventory.Infrastructure.Monitoring;

namespace SalesInventory.Worker;

/// <summary>
/// Escribe periódicamente el latido de esta instancia en SQL Server, independiente del bucle de procesamiento: así «el
/// proceso vive» y «el procesamiento funciona» se informan por separado (este último vía <see cref="WorkerCycleTracker"/>).
/// Un fallo al escribir el latido nunca detiene al Worker.
/// </summary>
public sealed class WorkerHeartbeatService(
    IServiceScopeFactory scopes,
    WorkerInstanceInfo instance,
    WorkerCycleTracker tracker,
    IOptions<SystemHealthOptions> options,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        settings.Validate();

        using var timer = new PeriodicTimer(settings.HeartbeatInterval);
        do
        {
            await BeatAsync(settings, stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task BeatAsync(SystemHealthOptions settings, CancellationToken stoppingToken)
    {
        var (succeeded, errorType) = tracker.Take();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(settings.QueryTimeout);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkerHeartbeatStore>()
                .BeatAsync(instance, succeeded, errorType, timeout.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado
        }
        catch (Exception exception)
        {
            // El éxito observado se conserva para el próximo latido.
            if (succeeded)
                tracker.KeepSuccess();

            logger.LogWarning("No se pudo escribir el latido del Worker ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
