using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SalesInventory.Application.Observability;

namespace SalesInventory.Infrastructure.Observability;

public static class TelemetryServiceCollectionExtensions
{
    public const string ApiServiceName = "SalesInventory.Api";
    public const string WorkerServiceName = "SalesInventory.Worker";

    /// <summary>
    /// Configura OpenTelemetry (trazas, métricas y logs) con Resource diferenciado por servicio. SQL Server se instrumenta
    /// SIN texto ni parámetros de las sentencias. Cada host añade su instrumentación específica (p. ej. ASP.NET Core).
    /// Exportación: consola en Development (configurable) y OTLP solo si hay endpoint configurado.
    /// </summary>
    public static IServiceCollection AddSalesInventoryTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!options.Enabled)
        {
            return services;
        }

        var console = options.ConsoleExporter ?? environment.IsDevelopment();
        var otlp = ResolveOtlpEndpoint(options);
        var hasOtlp = otlp.Configured;

        static void ConfigureOtlp(OtlpExporterOptions exporter, Uri? endpoint)
        {
            if (endpoint is not null)
            {
                exporter.Endpoint = endpoint;
            }
        }

        void DescribeResource(ResourceBuilder resource) => resource
            .AddService(serviceName: serviceName, serviceVersion: AppTelemetry.Version)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environment.EnvironmentName)]);

        services.AddOpenTelemetry()
            .ConfigureResource(DescribeResource)
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AppTelemetry.Name)
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation(sql =>
                    {
                        // Nunca texto ni parámetros de SQL (podrían contener datos sensibles).
                        sql.SetDbStatementForText = false;
                        sql.RecordException = false;
                    });
                configureTracing?.Invoke(tracing);
                if (console) tracing.AddConsoleExporter();
                if (hasOtlp) tracing.AddOtlpExporter(exporter => ConfigureOtlp(exporter, otlp.Endpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppTelemetry.Name)
                    .AddHttpClientInstrumentation();
                configureMetrics?.Invoke(metrics);
                if (console) metrics.AddConsoleExporter();
                if (hasOtlp) metrics.AddOtlpExporter(exporter => ConfigureOtlp(exporter, otlp.Endpoint));
            });

        // Logs: ILogger de siempre (la consola local los sigue mostrando); OpenTelemetry los exporta con TraceId/SpanId
        // solo hacia OTLP. No se implementa ningún sistema de logs propio.
        if (hasOtlp)
        {
            services.AddLogging(logging => logging.AddOpenTelemetry(log =>
            {
                var resource = ResourceBuilder.CreateDefault();
                DescribeResource(resource);
                log.SetResourceBuilder(resource);
                log.IncludeFormattedMessage = true;
                log.IncludeScopes = false;
                log.ParseStateValues = true;
                log.AddOtlpExporter(exporter => ConfigureOtlp(exporter, otlp.Endpoint));
            }));
        }

        return services;
    }

    private static (bool Configured, Uri? Endpoint) ResolveOtlpEndpoint(TelemetryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            if (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException("Telemetry:OtlpEndpoint debe ser una URL absoluta válida.");
            }

            return (true, endpoint);
        }

        // Variable estándar de OpenTelemetry: el exportador la lee por sí mismo.
        var fromEnvironment = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return (!string.IsNullOrWhiteSpace(fromEnvironment), null);
    }
}
