namespace SalesInventory.Infrastructure.Monitoring;

/// <summary>Sección <c>SystemHealth</c>: latido del Worker y umbrales del centro de monitoreo.</summary>
public sealed class SystemHealthOptions
{
    public const string SectionName = "SystemHealth";

    /// <summary>Cada cuánto escribe cada instancia del Worker su latido.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Sin latido durante este tiempo (reloj de SQL Server) una instancia se considera inactiva.</summary>
    public TimeSpan HeartbeatThreshold { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Tiempo máximo de cada consulta del endpoint; al vencer, esa sección se reporta como no disponible.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Un pendiente más antiguo que esto genera una advertencia de acumulación.</summary>
    public TimeSpan BacklogWarningAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Intervalos horarios con datos necesarios para publicar la serie temporal.</summary>
    public int SeriesMinimumBuckets { get; set; } = 2;

    public void Validate()
    {
        var errors = new List<string>();
        if (HeartbeatInterval < TimeSpan.FromSeconds(1) || HeartbeatInterval > TimeSpan.FromMinutes(5))
            errors.Add("SystemHealth:HeartbeatInterval debe estar entre 1 segundo y 5 minutos.");
        if (HeartbeatThreshold <= HeartbeatInterval)
            errors.Add("SystemHealth:HeartbeatThreshold debe ser mayor que HeartbeatInterval.");
        if (QueryTimeout < TimeSpan.FromMilliseconds(200) || QueryTimeout > TimeSpan.FromSeconds(60))
            errors.Add("SystemHealth:QueryTimeout debe estar entre 200 ms y 60 s.");
        if (BacklogWarningAfter <= TimeSpan.Zero)
            errors.Add("SystemHealth:BacklogWarningAfter debe ser positivo.");
        if (SeriesMinimumBuckets is < 1 or > 24)
            errors.Add("SystemHealth:SeriesMinimumBuckets debe estar entre 1 y 24.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Configuración de SystemHealth inválida: " + string.Join(" ", errors));
    }
}
