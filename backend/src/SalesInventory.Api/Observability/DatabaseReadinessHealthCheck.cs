using Microsoft.Extensions.Diagnostics.HealthChecks;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Api.Observability;

/// <summary>
/// Readiness: ¿puede la API atender solicitudes (SQL Server alcanzable)? El resultado público es solo
/// Healthy/Unhealthy: el motivo (excepción, servidor, cadena de conexión) nunca se expone, solo se registra el tipo.
/// </summary>
public sealed class DatabaseReadinessHealthCheck(SalesInventoryDbContext db, ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            return await db.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch (Exception exception)
        {
            logger.LogWarning("Readiness: la base de datos no está disponible ({ExceptionType}).", exception.GetType().Name);
            return HealthCheckResult.Unhealthy();
        }
    }
}
