namespace SalesInventory.Infrastructure.Observability;

/// <summary>Sección <c>Telemetry</c>. Sin configuración el sistema funciona igual: no hace falta ningún colector.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Interruptor general de OpenTelemetry (trazas, métricas y logs exportados).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Exporta trazas y métricas a la consola. Por defecto solo en Development.</summary>
    public bool? ConsoleExporter { get; set; }

    /// <summary>Endpoint OTLP opcional (p. ej. http://localhost:4317). También se respeta OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
    public string? OtlpEndpoint { get; set; }
}
