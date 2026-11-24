using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Auth.Interfaces;
using SalesInventory.Application.Monitoring;
using SalesInventory.Infrastructure.Auditing;
using SalesInventory.Infrastructure.Monitoring;
using SalesInventory.Infrastructure.Persistence;
using SalesInventory.Infrastructure.Security;

namespace SalesInventory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<SalesInventoryDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseSqlServer(connectionString, sqlOptions =>
                {
                    sqlOptions.MigrationsAssembly(typeof(SalesInventoryDbContext).Assembly.FullName);
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                });
            }
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SalesInventoryDbContext>());
        // Actor de auditoría por defecto (System); cada host lo reemplaza (API: usuario del JWT; Worker: System/Worker).
        services.TryAddScoped<IAuditActorProvider, SystemAuditActorProvider>();
        // Centro de monitoreo: umbrales configurables (sección SystemHealth) y servicio de estado operativo.
        services.Configure<SystemHealthOptions>(configuration.GetSection(SystemHealthOptions.SectionName));
        services.AddScoped<ISystemHealthService, SystemHealthService>();
        services.AddScoped<WorkerHeartbeatStore>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        return services;
    }
}
