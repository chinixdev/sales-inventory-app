namespace SalesInventory.Domain.Entities;

/// <summary>
/// Latido persistente de una instancia del Worker (una fila por instancia). Todas las marcas de tiempo las escribe SQL Server
/// (SYSUTCDATETIME()), de modo que varias máquinas comparten una única referencia de tiempo. Un latido reciente prueba que
/// el proceso está vivo, NO que el procesamiento funcione: para eso sirven el último ciclo correcto y el último error.
/// </summary>
public class WorkerHeartbeat
{
    public Guid InstanceId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>Último ciclo de sondeo del Outbox que terminó sin error (puede no haber tenido trabajo).</summary>
    public DateTime? LastSuccessfulCycleAtUtc { get; set; }

    /// <summary>Tipo (solo el nombre, nunca el mensaje) de la excepción del último ciclo, o nulo si el último ciclo fue correcto.</summary>
    public string? LastCycleErrorType { get; set; }

    public string Version { get; set; } = string.Empty;
}
