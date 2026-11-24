using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SalesInventory.Application.Outbox;

namespace SalesInventory.Infrastructure.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Registra el procesamiento del Outbox con la configuración de la sección <c>Outbox</c>.</summary>
    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        return AddCore(services);
    }

    /// <summary>Registra el procesamiento del Outbox con opciones definidas en código (pruebas).</summary>
    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services, Action<OutboxOptions> configure)
    {
        services.Configure(configure);
        return AddCore(services);
    }

    private static IServiceCollection AddCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxBackoffCalculator>();
        services.TryAddSingleton<OutboxProcessor>();
        services.TryAddScoped<OutboxStore>();
        services.TryAddScoped<OutboxDispatcher>();
        services.AddScoped<IOutboxEventHandler, SaleCompletedOutboxHandler>();
        services.AddScoped<IOutboxEventHandler, SaleStockAlertsOutboxHandler>();
        return services;
    }
}
