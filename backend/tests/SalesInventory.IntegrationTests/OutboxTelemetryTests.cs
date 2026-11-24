using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.IntegrationTests;

/// <summary>Captura actividades terminadas y mediciones de AppTelemetry (global: la colección SQL no corre en paralelo).</summary>
public sealed class TelemetryProbe : IDisposable
{
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly object _gate = new();
    private readonly List<Activity> _stopped = [];
    private readonly List<Measurement> _measurements = [];

    public TelemetryProbe()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (_gate) _stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(_activities);
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AppTelemetry.Name)
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _meters.Start();
    }

    private void Add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
        lock (_gate) _measurements.Add(new Measurement(instrument, value, copy));
    }

    public IReadOnlyList<Activity> Activities { get { lock (_gate) return _stopped.ToList(); } }

    public double Sum(string instrument, string? tag = null, object? value = null)
    {
        lock (_gate)
        {
            return _measurements
                .Where(m => m.Instrument == instrument && (tag is null || (m.Tags.TryGetValue(tag, out var v) && Equals(v, value))))
                .Sum(m => m.Value);
        }
    }

    public void Dispose()
    {
        _meters.Dispose();
        _activities.Dispose();
    }
}

/// <summary>Propagación de contexto, enlaces y métricas del procesamiento del Outbox contra SQL Server real.</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class OutboxTelemetryTests(SqlServerConcurrencyDatabaseFixture database)
{
    private ServiceProvider BuildProvider(OutboxTestProbe probe, int maxAttempts = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SalesInventoryDbContext>(options =>
            options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SalesInventoryDbContext>());
        services.AddScoped<DeferredMetrics>();
        services.AddSingleton(probe);
        services.AddSingleton(sp => new OutboxBackoffCalculator(sp.GetRequiredService<IOptions<OutboxOptions>>(), () => 1.0));
        services.AddOutboxProcessing(options =>
        {
            options.BatchSize = 10;
            options.LeaseDuration = TimeSpan.FromSeconds(60);
            options.HandlerTimeout = TimeSpan.FromSeconds(5);
            options.MaxAttempts = maxAttempts;
            options.BaseRetryDelay = TimeSpan.FromSeconds(10);
            options.MaxRetryDelay = TimeSpan.FromMinutes(10);
        });
        services.AddScoped<IOutboxEventHandler, OutboxTestProbeHandler>();
        return services.BuildServiceProvider();
    }

    private async Task ResetAsync()
    {
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.ProcessedEvents.ExecuteDeleteAsync();
    }

    private async Task<DateTime> SqlNowAsync()
    {
        await using var context = database.CreateDbContext();
        return await context.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync();
    }

    private async Task<Guid> SeedAsync(string? traceParent = null)
    {
        await using var context = database.CreateDbContext();
        var message = OutboxMessage.CreatePending(
            Guid.NewGuid(), OutboxTestProbe.EventType, "{}", (await SqlNowAsync()).AddMinutes(-1), traceParent);
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        return message.Id;
    }

    private async Task MakeDueAsync(Guid id)
    {
        var due = (await SqlNowAsync()).AddSeconds(-1);
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.Where(m => m.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NextAttemptAtUtc, due));
    }

    private static Activity Process(TelemetryProbe probe) =>
        probe.Activities.Single(a => a.OperationName == "outbox.process");

    [Fact]
    public async Task SaleCreatedThroughTheApi_PersistsTheW3CContextInItsOutboxMessage()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        var seeded = await database.SeedProductsAsync(5);

        using var response = await database.PostSaleAsync(
            new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var context = database.CreateDbContext();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync();
        message.TraceParent.Should().NotBeNullOrEmpty();
        TraceContextPropagation.TryParse(message.TraceParent, message.TraceState, out var origin).Should().BeTrue();
        probe.Activities.Select(a => a.TraceId).Should().Contain(origin.TraceId);
    }

    [Fact]
    public async Task Processing_IsItsOwnTraceLinkedToTheOriginalOperation()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        ActivityContext origin;
        using (var originActivity = AppTelemetry.Source.StartActivity("origen"))
        {
            origin = originActivity!.Context;
        }

        await SeedAsync(traceParent: $"00-{origin.TraceId}-{origin.SpanId}-01");
        await using var provider = BuildProvider(new OutboxTestProbe());

        await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        var processing = Process(probe);
        var link = processing.Links.Should().ContainSingle().Subject;
        link.Context.TraceId.Should().Be(origin.TraceId);
        link.Context.SpanId.Should().Be(origin.SpanId);
        processing.TraceId.Should().NotBe(origin.TraceId, "el procesamiento es otra traza, no reutiliza identificadores ajenos");
        processing.ParentSpanId.Should().Be(default(ActivitySpanId));
        processing.GetTagItem("outbox.outcome").Should().Be("processed");
        probe.Activities.Should().Contain(a => a.OperationName == "outbox.claim");
    }

    [Fact]
    public async Task OldEventWithoutContext_IsProcessedNormallyWithoutALink()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        await SeedAsync(traceParent: null);
        await using var provider = BuildProvider(new OutboxTestProbe());

        var claimed = await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        claimed.Should().Be(1);
        Process(probe).Links.Should().BeEmpty();
        probe.Sum("salesinventory.outbox.events.processed", "event_type", OutboxTestProbe.EventType).Should().Be(1);
    }

    [Fact]
    public async Task ARetriedThenSuccessfulEvent_CountsOneRetryAndOneProcessed_NeverDuplicates()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(new OutboxTestProbe { FailFirstAttempts = 1 });
        var processor = provider.GetRequiredService<OutboxProcessor>();

        await processor.ProcessBatchAsync(CancellationToken.None);
        await MakeDueAsync(id);
        await processor.ProcessBatchAsync(CancellationToken.None);

        probe.Sum("salesinventory.outbox.events.retried").Should().Be(1);
        probe.Sum("salesinventory.outbox.events.processed").Should().Be(1);
        probe.Sum("salesinventory.outbox.events.failed").Should().Be(0);
    }

    [Fact]
    public async Task AnEventThatExhaustsItsAttempts_CountsRetriesAndOneFailure()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(new OutboxTestProbe { AlwaysFail = true }, maxAttempts: 3);
        var processor = provider.GetRequiredService<OutboxProcessor>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
            await MakeDueAsync(id);
        }

        probe.Sum("salesinventory.outbox.events.retried").Should().Be(2);
        probe.Sum("salesinventory.outbox.events.failed", "reason", "exhausted").Should().Be(1);
        probe.Sum("salesinventory.outbox.events.processed").Should().Be(0);
    }

    [Fact]
    public async Task EventMetricsAndTraces_NeverExposeIdentifiersOrErrorText()
    {
        await ResetAsync();
        using var probe = new TelemetryProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(new OutboxTestProbe { AlwaysFail = true });

        await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        var values = probe.Activities.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}");
        values.Should().NotContain(v => v.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));
        values.Should().NotContain(v => v.Contains("hunter2", StringComparison.OrdinalIgnoreCase));
    }
}
