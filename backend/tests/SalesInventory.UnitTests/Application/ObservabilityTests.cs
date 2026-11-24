using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Application.Sales.Services;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Auditing;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

/// <summary>Las métricas y las trazas son estáticas y globales: estas pruebas no se ejecutan en paralelo con otras.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}

/// <summary>Captura las actividades terminadas y las mediciones de AppTelemetry durante una prueba.</summary>
public sealed class TelemetryCapture : IDisposable
{
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly object _gate = new();
    private readonly List<Activity> _stopped = [];
    private readonly List<Measurement> _measurements = [];

    public TelemetryCapture()
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

    public IReadOnlyList<Measurement> Of(string instrument)
    {
        lock (_gate) return _measurements.Where(m => m.Instrument == instrument).ToList();
    }

    public double Sum(string instrument) => Of(instrument).Sum(m => m.Value);

    public void Dispose()
    {
        _meters.Dispose();
        _activities.Dispose();
    }
}

[Collection(TelemetryCollection.Name)]
public sealed class ObservabilityTests
{
    private static SalesInventoryDbContext CreateContext(IAuditActorProvider? actor = null) =>
        new(new DbContextOptionsBuilder<SalesInventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            actor);

    private static async Task<(SalesInventoryDbContext Context, User User, Product Product)> SeedAsync(int stock = 20)
    {
        var context = CreateContext();
        var category = new Category { Name = "Cat" };
        var user = new User { FullName = "Vendedor", Email = "v@test.com", PasswordHash = "h", Role = UserRole.Admin };
        context.AddRange(category, user);
        await context.SaveChangesAsync();
        var product = new Product { CategoryId = category.Id, SKU = "P1", Name = "Teclado", Price = 50m, Stock = stock, MinimumStock = 2, IsActive = true };
        context.Add(product);
        await context.SaveChangesAsync();
        return (context, user, product);
    }

    private static CreateSaleRequestDto Request(User user, Product product, int quantity, Guid? key = null) => new()
    {
        IdempotencyKey = key ?? Guid.NewGuid(),
        UserId = user.Id,
        Items = [new SaleItemRequestDto { ProductId = product.Id, Quantity = quantity }]
    };

    // ─── Propagación de contexto W3C ───

    [Fact]
    public void Capture_StoresTheW3CContextOfTheCurrentActivity_AndItCanBeParsedBack()
    {
        using var capture = new TelemetryCapture();
        using var activity = AppTelemetry.Source.StartActivity("origen");
        activity.Should().NotBeNull();

        var (traceParent, _) = TraceContextPropagation.Capture();

        traceParent.Should().Be(activity!.Id);
        TraceContextPropagation.TryParse(traceParent, null, out var context).Should().BeTrue();
        context.TraceId.Should().Be(activity.TraceId);
        context.SpanId.Should().Be(activity.SpanId);
        context.IsRemote.Should().BeTrue();
    }

    [Fact]
    public void Capture_WithoutAnActivity_ReturnsNothing()
    {
        Activity.Current = null;

        TraceContextPropagation.Capture().Should().Be(((string?)null, (string?)null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("basura")]
    [InlineData("00-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz-zzzzzzzzzzzzzzzz-01")]
    public void TryParse_OldOrMalformedContext_ProducesNoLinkAndNeverThrows(string? traceParent)
    {
        TraceContextPropagation.TryParse(traceParent, null, out _).Should().BeFalse();
    }

    [Fact]
    public void SaleCompletedMessage_CarriesTheContextOfTheOriginalOperation_AndNoneWithoutTrace()
    {
        var sale = new Sale { Id = 1, UserId = 1, Total = 10m, SaleDate = DateTime.UtcNow };
        SaleDetail[] details = [new SaleDetail { ProductId = 1, Quantity = 1, UnitPrice = 10m }];

        using (var capture = new TelemetryCapture())
        using (var activity = AppTelemetry.Source.StartActivity("origen"))
        {
            SaleCompletedV1.ToOutboxMessage(sale, details).TraceParent.Should().Be(activity!.Id);
        }

        Activity.Current = null;
        var legacyLike = SaleCompletedV1.ToOutboxMessage(sale, details);
        legacyLike.TraceParent.Should().BeNull();
        legacyLike.TraceState.Should().BeNull();
    }

    [Fact]
    public void ClaimedMessage_WithoutTraceContext_IsCompatible()
    {
        var legacy = OutboxMessage.CreatePending(Guid.NewGuid(), "SaleCompleted.v1", "{}", DateTime.UtcNow);
        legacy.LockId = Guid.NewGuid();

        var claimed = ClaimedOutboxMessage.From(legacy);

        claimed.TraceParent.Should().BeNull();
        TraceContextPropagation.TryParse(claimed.TraceParent, claimed.TraceState, out _).Should().BeFalse();
    }

    // ─── Ventas: trazas y métricas ───

    [Fact]
    public async Task ConfirmedSale_RecordsOneMetricOneTraceAndStoresTheContextOfOutboxPersistence()
    {
        using var capture = new TelemetryCapture();
        var (context, user, product) = await SeedAsync();

        await new SaleService(context).CreateAsync(Request(user, product, 2));

        capture.Sum("salesinventory.sales.confirmed").Should().Be(1);
        capture.Of("salesinventory.sales.idempotency.replays").Should().BeEmpty();
        capture.Of("salesinventory.sales.duration").Should().ContainSingle()
            .Which.Tags["outcome"].Should().Be("confirmed");

        var sales = capture.Activities.Single(a => a.OperationName == "sales.create");
        var persist = capture.Activities.Single(a => a.OperationName == "outbox.persist");
        persist.ParentId.Should().Be(sales.Id);
        sales.GetTagItem("sales.outcome").Should().Be("confirmed");

        var message = await context.OutboxMessages.SingleAsync();
        message.TraceParent.Should().Be(persist.Id, "el evento enlaza con la operación original, no con otro span");
    }

    [Fact]
    public async Task IdempotentReplay_CountsAReplayAndNeverAnotherConfirmedSale()
    {
        using var capture = new TelemetryCapture();
        var (context, user, product) = await SeedAsync();
        var key = Guid.NewGuid();
        var service = new SaleService(context);

        await service.CreateAsync(Request(user, product, 1, key));
        await service.CreateAsync(Request(user, product, 1, key));

        capture.Sum("salesinventory.sales.confirmed").Should().Be(1);
        capture.Of("salesinventory.sales.idempotency.replays").Should().ContainSingle()
            .Which.Tags["reason"].Should().Be("sequential");
        capture.Of("salesinventory.sales.duration").Select(m => m.Tags["outcome"])
            .Should().Equal("confirmed", "replayed");
        (await context.OutboxMessages.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RejectedSale_IsNotCountedAsConfirmed_AndIsNotATraceError()
    {
        using var capture = new TelemetryCapture();
        var (context, user, product) = await SeedAsync(stock: 1);

        var act = async () => await new SaleService(context).CreateAsync(Request(user, product, 5));

        await act.Should().ThrowAsync<Exception>();
        capture.Sum("salesinventory.sales.confirmed").Should().Be(0);
        capture.Of("salesinventory.sales.duration").Should().ContainSingle()
            .Which.Tags["outcome"].Should().Be("rejected");
        capture.Activities.Single(a => a.OperationName == "sales.create").Status.Should().Be(ActivityStatusCode.Unset);
    }

    /// <summary>
    /// Simula la estrategia de reintentos: el delegado de la venta se ejecuta COMPLETO una vez, el commit «falla» y se repite
    /// desde cero. (InMemory no revierte: aquí solo se comprueba que las métricas se registran por solicitud, no por intento.)
    /// </summary>
    private sealed class RetryingContext(SalesInventoryDbContext inner) : IApplicationDbContext
    {
        public int Attempts { get; private set; }

        public DbSet<User> Users => inner.Users;
        public DbSet<Category> Categories => inner.Categories;
        public DbSet<Product> Products => inner.Products;
        public DbSet<Sale> Sales => inner.Sales;
        public DbSet<SaleDetail> SaleDetails => inner.SaleDetails;
        public DbSet<StockMovement> StockMovements => inner.StockMovements;
        public DbSet<IdempotencyRecord> IdempotencyRecords => inner.IdempotencyRecords;
        public DbSet<OutboxMessage> OutboxMessages => inner.OutboxMessages;
        public DbSet<ProcessedEvent> ProcessedEvents => inner.ProcessedEvents;
        public DbSet<Notification> Notifications => inner.Notifications;
        public IQueryable<AuditLog> AuditLogs => inner.AuditLogs;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);
        public void ClearChangeTracker() => inner.ClearChangeTracker();
        public bool IsDuplicateIdempotencyKey(DbUpdateException exception) => inner.IsDuplicateIdempotencyKey(exception);

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            Attempts++;
            await operation(cancellationToken);
            Attempts++;
            return await operation(cancellationToken);
        }
    }

    [Fact]
    public async Task InternalTransactionRetries_DoNotDuplicateTheSuccessMetric()
    {
        using var capture = new TelemetryCapture();
        var (context, user, product) = await SeedAsync();
        var retrying = new RetryingContext(context);

        await new SaleService(retrying).CreateAsync(Request(user, product, 1));

        retrying.Attempts.Should().Be(2);
        capture.Sum("salesinventory.sales.confirmed").Should().Be(1);
        capture.Of("salesinventory.sales.duration").Should().ContainSingle();
        capture.Activities.Count(a => a.OperationName == "sales.create").Should().Be(1);
    }

    // ─── Métricas diferidas (efectos del Outbox) ───

    [Fact]
    public void DeferredMetrics_AreOnlyRecordedOnFlush_AndAnAttemptThatIsRepeatedNeverDuplicates()
    {
        using var capture = new TelemetryCapture();
        var deferred = new DeferredMetrics();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            deferred.Reset(); // cada intento de la estrategia parte de cero
            deferred.Add(() => AppTelemetry.NotificationsGenerated.Add(2, new KeyValuePair<string, object?>("type", "LowStock")));
        }

        capture.Of("salesinventory.notifications.generated").Should().BeEmpty("nada se contabiliza antes del commit");

        deferred.Flush();
        deferred.Flush();

        capture.Sum("salesinventory.notifications.generated").Should().Be(2);
    }

    [Fact]
    public async Task StockAlertsHandler_DefersTheNotificationMetric_AndTracesTheGeneration()
    {
        using var capture = new TelemetryCapture();
        var context = CreateContext();
        var category = new Category { Name = "Cat" };
        var admin = new User { FullName = "Admin", Email = "a@x.pe", PasswordHash = "h", Role = UserRole.Admin, IsActive = true };
        var product = new Product { Category = category, SKU = "P-1", Name = "Teclado", Price = 10m, Stock = 1, MinimumStock = 5, IsActive = true };
        context.AddRange(category, admin, product);
        await context.SaveChangesAsync();
        var message = SaleCompletedV1.ToOutboxMessage(
            new Sale { Id = 3, UserId = admin.Id, Total = 10m, SaleDate = DateTime.UtcNow },
            [new SaleDetail { ProductId = product.Id, Quantity = 1, UnitPrice = 10m }]);
        var deferred = new DeferredMetrics();
        var handler = new SalesInventory.Application.Outbox.SaleStockAlertsOutboxHandler(context, TimeProvider.System, deferred);

        await handler.HandleAsync(
            new SalesInventory.Application.Outbox.OutboxEnvelope(message.Id, message.EventType, message.Payload, message.OccurredAtUtc),
            CancellationToken.None);

        capture.Of("salesinventory.notifications.generated").Should().BeEmpty();
        capture.Activities.Single(a => a.OperationName == "notifications.generate").GetTagItem("notifications.created").Should().Be(1);

        deferred.Flush();

        var measurement = capture.Of("salesinventory.notifications.generated").Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags["type"].Should().Be("LowStock");
    }

    // ─── Correlación con la auditoría ───

    [Fact]
    public void AuditCorrelation_UsesTheTraceIdOfTheCurrentActivity_AndTheOwnIdWithout()
    {
        var provider = new WorkerAuditActorProvider();
        Activity.Current = null;
        var own = provider.GetCurrent().CorrelationId;
        provider.GetCurrent().CorrelationId.Should().Be(own);

        using var capture = new TelemetryCapture();
        using var activity = AppTelemetry.Source.StartActivity("origen");

        provider.GetCurrent().CorrelationId.Should().Be(activity!.TraceId.ToString());
        provider.GetCurrent().CorrelationId.Should().NotBe(own);
    }

    // ─── Higiene de etiquetas ───

    [Fact]
    public async Task Metrics_NeverUseHighCardinalityOrSensitiveTags()
    {
        using var capture = new TelemetryCapture();
        var (context, user, product) = await SeedAsync();
        var key = Guid.NewGuid();

        await new SaleService(context).CreateAsync(Request(user, product, 1, key));

        var allowed = new[] { "outcome", "reason", "type", "event_type", "source" };
        var tags = new[]
        {
            "salesinventory.sales.confirmed", "salesinventory.sales.duration", "salesinventory.sales.idempotency.replays"
        }.SelectMany(capture.Of).SelectMany(m => m.Tags.Keys).Distinct();
        tags.Should().BeSubsetOf(allowed);

        var activityValues = capture.Activities.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}");
        activityValues.Should().NotContain(v => v.Contains(key.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
