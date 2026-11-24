using SalesInventory.Application;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Infrastructure;
using SalesInventory.Infrastructure.Auditing;
using SalesInventory.Infrastructure.Monitoring;
using SalesInventory.Infrastructure.Observability;
using SalesInventory.Application.Observability;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Reutiliza Application e Infrastructure (DbContext, EnableRetryOnFailure, servicios) sin pasar por la API.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOutboxProcessing(builder.Configuration);

// Observabilidad (OpenTelemetry): Resource SalesInventory.Worker; consola en Development y OTLP opcional.
builder.Services.AddSalesInventoryTelemetry(
    builder.Configuration, builder.Environment, TelemetryServiceCollectionExtensions.WorkerServiceName);

// Auditoría: todo cambio auditable hecho por el Worker se registra como System/Worker (sin usuario).
builder.Services.AddScoped<IAuditActorProvider, WorkerAuditActorProvider>();

// Latido persistente (instancia, última actividad, inicio y versión) y resultado de los ciclos, para el centro de monitoreo.
builder.Services.AddSingleton(WorkerInstanceInfo.Create(AppTelemetry.Version));
builder.Services.AddSingleton<WorkerCycleTracker>();
builder.Services.AddHostedService<WorkerHeartbeatService>();
builder.Services.AddHostedService<OutboxWorker>();

// Apagado ordenado: da tiempo a terminar el mensaje en curso y liberar los reclamados sin empezar.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

await builder.Build().RunAsync();
