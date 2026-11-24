using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.IntegrationTests;

/// <summary>Estado compartido entre los handlers de prueba y el test (varias instancias de worker comparten el mismo).</summary>
public sealed class OutboxTestProbe
{
    public const string EventType = "Test.v1";
    public const string Consumer = "test.probe";

    public ConcurrentDictionary<Guid, int> Invocations { get; } = new();
    public int FailFirstAttempts { get; set; }
    public bool AlwaysFail { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public Action<Guid>? OnInvoked { get; set; }

    public int Register(Guid eventId)
    {
        var attempt = Invocations.AddOrUpdate(eventId, 1, (_, count) => count + 1);
        OnInvoked?.Invoke(eventId);
        return attempt;
    }
}

/// <summary>Handler de prueba: escribe un efecto local (ProcessedEvent) y después puede fallar o demorarse.</summary>
public sealed class OutboxTestProbeHandler(IApplicationDbContext context, OutboxTestProbe probe) : IOutboxEventHandler
{
    public string EventType => OutboxTestProbe.EventType;

    public string ConsumerName => OutboxTestProbe.Consumer;

    public async Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        var attempt = probe.Register(message.EventId);

        var exists = await context.ProcessedEvents.AnyAsync(
            p => p.EventId == message.EventId && p.Consumer == OutboxTestProbe.Consumer, cancellationToken);
        if (!exists)
        {
            context.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = message.EventId,
                Consumer = OutboxTestProbe.Consumer,
                EventType = message.EventType,
                ProcessedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        if (probe.Delay > TimeSpan.Zero)
        {
            await Task.Delay(probe.Delay, cancellationToken);
        }

        if (probe.AlwaysFail || attempt <= probe.FailFirstAttempts)
        {
            // El efecto ya se escribió: la transacción debe revertirlo todo al fallar.
            throw new InvalidOperationException("Fallo simulado del consumidor password=hunter2");
        }
    }
}

/// <summary>
/// Procesamiento del Outbox contra SQL Server real. Los vencimientos, leases y backoff usan el reloj de SQL Server,
/// así que el «paso del tiempo» se simula desplazando las marcas de tiempo persistidas, no un reloj en memoria.
/// </summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class OutboxProcessorTests(SqlServerConcurrencyDatabaseFixture database)
{
    private ServiceProvider BuildProvider(OutboxTestProbe probe, Action<OutboxOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SalesInventoryDbContext>(options =>
            options.UseSqlServer(database.ConnectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SalesInventoryDbContext>());
        services.AddSingleton(probe);
        // Jitter fijo (factor 1.0) para que el backoff sea exactamente base · 2^(intento−1).
        services.AddSingleton(sp => new OutboxBackoffCalculator(sp.GetRequiredService<IOptions<OutboxOptions>>(), () => 1.0));
        services.AddOutboxProcessing(options =>
        {
            options.BatchSize = 10;
            options.LeaseDuration = TimeSpan.FromSeconds(60);
            options.HandlerTimeout = TimeSpan.FromSeconds(5);
            options.MaxAttempts = 3;
            options.BaseRetryDelay = TimeSpan.FromSeconds(10);
            options.MaxRetryDelay = TimeSpan.FromMinutes(10);
            configure?.Invoke(options);
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

    private async Task<Guid> SeedAsync(string eventType = OutboxTestProbe.EventType, string payload = "{}")
    {
        await using var context = database.CreateDbContext();
        var message = OutboxMessage.CreatePending(Guid.NewGuid(), eventType, payload, (await SqlNowAsync()).AddMinutes(-1));
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        return message.Id;
    }

    private async Task<OutboxMessage> GetAsync(Guid id)
    {
        await using var context = database.CreateDbContext();
        return await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task<int> EffectsAsync(string consumer = OutboxTestProbe.Consumer)
    {
        await using var context = database.CreateDbContext();
        return await context.ProcessedEvents.CountAsync(p => p.Consumer == consumer);
    }

    /// <summary>Simula que el backoff ya venció.</summary>
    private async Task MakeDueAsync(Guid id)
    {
        var due = (await SqlNowAsync()).AddSeconds(-1);
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.Where(m => m.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NextAttemptAtUtc, due));
    }

    /// <summary>Simula que el lease del mensaje Processing ya venció.</summary>
    private async Task ExpireLeaseAsync(Guid id)
    {
        var expired = (await SqlNowAsync()).AddMinutes(-1);
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.Where(m => m.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.LockedUntilUtc, expired));
    }

    // ─── Procesamiento exitoso ───────────────────────────────────────────────

    [Fact]
    public async Task SaleCompletedEvent_IsProcessed_AndLeavesAPersistentProcessingRecord()
    {
        await ResetAsync();
        var seeded = await database.SeedProductsAsync(5);
        using var response = await database.PostSaleAsync(
            new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 2 } } });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var provider = BuildProvider(new OutboxTestProbe());

        var before = await SqlNowAsync();
        // La venta recién creada puede no estar vencida según el reloj de SQL: se adelanta su NextAttemptAtUtc.
        await using (var context = database.CreateDbContext())
        {
            await context.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, before.AddMinutes(-1)));
        }

        var claimed = await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);
        var after = await SqlNowAsync();

        claimed.Should().Be(1);
        await using var check = database.CreateDbContext();
        var message = await check.OutboxMessages.AsNoTracking().SingleAsync();
        message.Status.Should().Be(OutboxMessageStatus.Processed);
        message.ProcessedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after, "usa el reloj de SQL Server");
        message.LockId.Should().BeNull();
        message.LockedUntilUtc.Should().BeNull();
        message.RetryCount.Should().Be(0);
        message.LastError.Should().BeNull();

        // Un mismo evento tiene dos consumidores independientes, cada uno con su registro único.
        var records = await check.ProcessedEvents.AsNoTracking().Where(p => p.EventId == message.Id).ToListAsync();
        records.Select(r => r.Consumer).Should().BeEquivalentTo(
            SaleCompletedOutboxHandler.Consumer, SaleStockAlertsOutboxHandler.Consumer);
        records.Single(r => r.Consumer == SaleCompletedOutboxHandler.Consumer).Summary.Should().Contain("2 unidad(es)");
    }

    // ─── Dos workers concurrentes ────────────────────────────────────────────

    [Fact]
    public async Task TwoConcurrentWorkers_NeverProcessTheSameMessageTwice()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var ids = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            ids.Add(await SeedAsync());
        }

        await using var workerA = BuildProvider(probe, o => o.BatchSize = 4);
        await using var workerB = BuildProvider(probe, o => o.BatchSize = 4);

        async Task RunAsync(ServiceProvider provider)
        {
            var processor = provider.GetRequiredService<OutboxProcessor>();
            while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
            {
            }
        }

        await Task.WhenAll(RunAsync(workerA), RunAsync(workerB));

        probe.Invocations.Keys.Should().BeEquivalentTo(ids);
        probe.Invocations.Values.Should().OnlyContain(count => count == 1, "cada mensaje lo procesa un único worker");
        await using var context = database.CreateDbContext();
        (await context.OutboxMessages.CountAsync(m => m.Status == OutboxMessageStatus.Processed)).Should().Be(30);
        (await EffectsAsync()).Should().Be(30);
    }

    // ─── Reintentos y backoff ────────────────────────────────────────────────

    [Fact]
    public async Task TransientFailures_AreRetriedWithPersistentExponentialBackoff_AndTheEffectIsRolledBack()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe { FailFirstAttempts = 2 };
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe);
        var processor = provider.GetRequiredService<OutboxProcessor>();

        // Intento 1: falla → Pending, backoff base (10 s) medido con el reloj de SQL, efecto revertido.
        var before = await SqlNowAsync();
        (await processor.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        var after = await SqlNowAsync();
        var afterFirst = await GetAsync(id);
        afterFirst.Status.Should().Be(OutboxMessageStatus.Pending);
        afterFirst.RetryCount.Should().Be(1);
        afterFirst.NextAttemptAtUtc.Should().BeOnOrAfter(before.AddSeconds(10)).And.BeOnOrBefore(after.AddSeconds(10));
        afterFirst.LockId.Should().BeNull();
        afterFirst.LastError.Should().Contain("InvalidOperationException").And.Contain("[redacted]").And.NotContain("hunter2");
        (await EffectsAsync()).Should().Be(0, "el efecto y la finalización comparten transacción");

        // Aún no vence el backoff: nadie lo reclama.
        (await processor.ProcessBatchAsync(CancellationToken.None)).Should().Be(0);

        // Intento 2: falla → backoff duplicado (20 s).
        await MakeDueAsync(id);
        before = await SqlNowAsync();
        (await processor.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        after = await SqlNowAsync();
        var afterSecond = await GetAsync(id);
        afterSecond.RetryCount.Should().Be(2);
        afterSecond.NextAttemptAtUtc.Should().BeOnOrAfter(before.AddSeconds(20)).And.BeOnOrBefore(after.AddSeconds(20));

        // Intento 3: tiene éxito.
        await MakeDueAsync(id);
        (await processor.ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        var final = await GetAsync(id);
        final.Status.Should().Be(OutboxMessageStatus.Processed);
        final.RetryCount.Should().Be(2);
        probe.Invocations[id].Should().Be(3);
        (await EffectsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExhaustedAttempts_MoveTheMessageToFailed_AndItIsNeverClaimedAgain()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe { AlwaysFail = true };
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe); // MaxAttempts = 3
        var processor = provider.GetRequiredService<OutboxProcessor>();

        for (var round = 0; round < 6; round++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
            await MakeDueAsync(id);
        }

        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Failed);
        message.RetryCount.Should().Be(3);
        message.LockId.Should().BeNull();
        message.LastError.Should().NotBeNullOrWhiteSpace();
        probe.Invocations[id].Should().Be(3, "no se reintenta más allá de MaxAttempts");
        (await EffectsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HandlerTimeout_IsTreatedAsATransientFailure()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe { Delay = TimeSpan.FromSeconds(10) };
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe, o => o.HandlerTimeout = TimeSpan.FromMilliseconds(300));

        await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.RetryCount.Should().Be(1);
        message.LastError.Should().Contain("Tiempo límite");
        (await EffectsAsync()).Should().Be(0);
    }

    // ─── Recuperación tras caída / lease vencido ─────────────────────────────

    [Fact]
    public async Task ExpiredLease_AfterACrash_IsReclaimedAndTheAbandonedAttemptIsCounted()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await SimulateCrashAsync(id, leaseExpired: true, retryCount: 0);
        await using var provider = BuildProvider(probe);

        (await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None)).Should().Be(1);

        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Processed);
        message.RetryCount.Should().Be(1, "el intento abandonado por la caída cuenta como consumido");
        probe.Invocations[id].Should().Be(1);
    }

    [Fact]
    public async Task ExpiredLease_WithNoAttemptsLeft_BecomesFailedInsteadOfLoopingForever()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await SimulateCrashAsync(id, leaseExpired: true, retryCount: 2); // MaxAttempts = 3
        await using var provider = BuildProvider(probe);

        (await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None)).Should().Be(0);

        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Failed);
        message.RetryCount.Should().Be(3);
        message.LastError.Should().Contain("Lease vencido");
        message.LockId.Should().BeNull();
        probe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveLease_IsNotStolenByAnotherWorker()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await SimulateCrashAsync(id, leaseExpired: false, retryCount: 0); // lease vigente
        await using var provider = BuildProvider(probe);

        (await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None)).Should().Be(0);

        (await GetAsync(id)).Status.Should().Be(OutboxMessageStatus.Processing);
        probe.Invocations.Should().BeEmpty();
    }

    private async Task SimulateCrashAsync(Guid id, bool leaseExpired, int retryCount)
    {
        var lockId = Guid.NewGuid();
        var now = await SqlNowAsync();
        var lockedUntil = leaseExpired ? now.AddSeconds(-5) : now.AddSeconds(30);
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(m => m.Status, OutboxMessageStatus.Processing)
            .SetProperty(m => m.LockId, lockId)
            .SetProperty(m => m.LockedUntilUtc, lockedUntil)
            .SetProperty(m => m.RetryCount, retryCount));
    }

    // ─── Worker antiguo vs. reclamo de otro; vigencia del lease ──────────────

    [Fact]
    public async Task StaleWorker_CannotCompleteOverwriteOrReleaseAMessageReclaimedByAnotherWorker()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe);

        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<OutboxStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<OutboxStore>();

        var claimedByA = (await storeA.ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        await ExpireLeaseAsync(id); // el lease de A vence
        var claimedByB = (await storeB.ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None)).Single();

        claimedByB.Id.Should().Be(id);
        claimedByB.LockId.Should().NotBe(claimedByA.LockId);
        claimedByB.RetryCount.Should().Be(1, "el reclamo de un lease vencido cuenta un intento abandonado");

        var staleEffects = 0;
        var complete = async () => await storeA.CompleteAsync(claimedByA, async token =>
        {
            staleEffects++;
            await Task.CompletedTask;
        }, CancellationToken.None);

        await complete.Should().ThrowAsync<LeaseLostException>();
        staleEffects.Should().Be(0, "el efecto ni siquiera se ejecuta si el lease no es propio");
        (await storeA.RecordFailureAsync(claimedByA, "viejo", permanent: true, TimeSpan.Zero, CancellationToken.None))
            .Should().BeFalse();
        (await storeA.ReleaseAsync(claimedByA, CancellationToken.None)).Should().BeFalse();

        var stillOwnedByB = await GetAsync(id);
        stillOwnedByB.Status.Should().Be(OutboxMessageStatus.Processing);
        stillOwnedByB.LockId.Should().Be(claimedByB.LockId);

        // B completa con su propio efecto.
        await storeB.CompleteAsync(claimedByB, async token =>
        {
            await using var effectContext = database.CreateDbContext();
            effectContext.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = id,
                Consumer = "test.stale",
                EventType = "Test.v1",
                ProcessedAtUtc = DateTime.UtcNow
            });
            await effectContext.SaveChangesAsync(token);
        }, CancellationToken.None);
        (await GetAsync(id)).Status.Should().Be(OutboxMessageStatus.Processed);

        // Un intento tardío de A sobre un mensaje ya procesado no lo modifica ni repite el efecto.
        await storeA.CompleteAsync(claimedByA, _ =>
        {
            staleEffects++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        staleEffects.Should().Be(0);
        (await EffectsAsync("test.stale")).Should().Be(1);
    }

    [Fact]
    public async Task CompletionWithAnExpiredLease_IsRejectedEvenIfNobodyReclaimedTheMessage()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();

        var claimed = (await store.ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        await ExpireLeaseAsync(id); // sigue siendo el LockId de este worker, pero ya no está vigente

        var effects = 0;
        var complete = async () => await store.CompleteAsync(claimed, _ =>
        {
            effects++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        await complete.Should().ThrowAsync<LeaseLostException>("la propiedad sin vigencia no basta para finalizar");
        effects.Should().Be(0);
        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Processing);
        message.ProcessedAtUtc.Should().BeNull();

        // El siguiente ciclo lo recupera y cuenta el intento abandonado.
        (await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None)).Should().Be(1);
        var recovered = await GetAsync(id);
        recovered.Status.Should().Be(OutboxMessageStatus.Processed);
        recovered.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task ClaimedLease_ExpiresAccordingToTheSqlServerClock()
    {
        await ResetAsync();
        var id = await SeedAsync();
        await using var provider = BuildProvider(new OutboxTestProbe(), o => o.LeaseDuration = TimeSpan.FromSeconds(90));
        await using var scope = provider.CreateAsyncScope();

        var before = await SqlNowAsync();
        await scope.ServiceProvider.GetRequiredService<OutboxStore>().ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None);
        var after = await SqlNowAsync();

        var claimed = await GetAsync(id);
        claimed.LockedUntilUtc.Should().BeOnOrAfter(before.AddSeconds(90)).And.BeOnOrBefore(after.AddSeconds(90));
    }

    // ─── Consumidor idempotente / rollback del efecto ────────────────────────

    [Fact]
    public async Task RepeatedDelivery_DoesNotDuplicateTheConsumerEffect()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe();
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe);
        var processor = provider.GetRequiredService<OutboxProcessor>();

        await processor.ProcessBatchAsync(CancellationToken.None);
        (await EffectsAsync()).Should().Be(1);

        // Entrega repetida (p. ej. commit incierto o reencolado manual): el mensaje vuelve a Pending.
        var due = (await SqlNowAsync()).AddSeconds(-1);
        await using (var context = database.CreateDbContext())
        {
            await context.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
                .SetProperty(m => m.ProcessedAtUtc, (DateTime?)null)
                .SetProperty(m => m.NextAttemptAtUtc, due));
        }

        await processor.ProcessBatchAsync(CancellationToken.None);

        probe.Invocations[id].Should().Be(2, "el mensaje se entregó dos veces");
        (await EffectsAsync()).Should().Be(1, "el efecto es único por (EventId, consumidor)");
        (await GetAsync(id)).Status.Should().Be(OutboxMessageStatus.Processed);
    }

    [Fact]
    public async Task ConsumerEffect_IsRolledBackTogetherWithTheMessageCompletion_WhenTheHandlerFails()
    {
        await ResetAsync();
        var probe = new OutboxTestProbe { FailFirstAttempts = 1 };
        var id = await SeedAsync();
        await using var provider = BuildProvider(probe);

        await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);

        (await EffectsAsync()).Should().Be(0);
        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.ProcessedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProcessedEvents_EnforcesUniquenessPerEventAndConsumer()
    {
        await ResetAsync();
        var eventId = Guid.NewGuid();
        await using var first = database.CreateDbContext();
        first.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, Consumer = "c", EventType = "t", ProcessedAtUtc = DateTime.UtcNow });
        await first.SaveChangesAsync();

        await using var second = database.CreateDbContext();
        second.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, Consumer = "c", EventType = "t", ProcessedAtUtc = DateTime.UtcNow });
        var duplicate = async () => await second.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>();

        await using var third = database.CreateDbContext();
        third.ProcessedEvents.Add(new ProcessedEvent { EventId = eventId, Consumer = "other", EventType = "t", ProcessedAtUtc = DateTime.UtcNow });
        var otherConsumer = async () => await third.SaveChangesAsync();
        await otherConsumer.Should().NotThrowAsync();
    }

    // ─── Eventos desconocidos y payloads inválidos ───────────────────────────

    [Theory]
    [InlineData("Unknown.v9", "{}", "handler")]
    [InlineData("SaleCompleted.v1", "{\"foo\":1}", "Versión de esquema no soportada")]
    [InlineData("SaleCompleted.v1", "[]", "no es un SaleCompleted.v1 válido")]
    [InlineData("SaleCompleted.v1", "{\"schemaVersion\":1,\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"saleId\":1,\"userId\":1,\"total\":1,\"occurredAtUtc\":\"2026-01-01T00:00:00Z\",\"items\":[{\"productId\":1,\"quantity\":1}]}", "no coincide")]
    public async Task UnknownEventOrInvalidPayload_FailsPermanentlyWithoutRetries(string eventType, string payload, string expectedError)
    {
        await ResetAsync();
        var id = await SeedAsync(eventType, payload);
        await using var provider = BuildProvider(new OutboxTestProbe());
        var processor = provider.GetRequiredService<OutboxProcessor>();

        await processor.ProcessBatchAsync(CancellationToken.None);
        await MakeDueAsync(id);
        (await processor.ProcessBatchAsync(CancellationToken.None)).Should().Be(0, "un error permanente no se reintenta");

        var message = await GetAsync(id);
        message.Status.Should().Be(OutboxMessageStatus.Failed);
        message.RetryCount.Should().Be(1);
        message.LastError.Should().Contain(expectedError);
        message.LockId.Should().BeNull();
        (await EffectsAsync(SaleCompletedOutboxHandler.Consumer)).Should().Be(0);
        (await EffectsAsync(SaleStockAlertsOutboxHandler.Consumer)).Should().Be(0);
    }

    // ─── Apagado ordenado ────────────────────────────────────────────────────

    [Fact]
    public async Task GracefulShutdown_FinishesTheInFlightMessage_AndReleasesTheOnesNotStarted()
    {
        await ResetAsync();
        using var stopping = new CancellationTokenSource();
        var probe = new OutboxTestProbe { OnInvoked = _ => stopping.Cancel() };
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await SeedAsync());
        }

        await using var provider = BuildProvider(probe);
        (await provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(stopping.Token)).Should().Be(3);

        var messages = await Task.WhenAll(ids.Select(GetAsync));
        messages.Count(m => m.Status == OutboxMessageStatus.Processed).Should().Be(1, "el mensaje en curso termina");
        var released = messages.Where(m => m.Status == OutboxMessageStatus.Pending).ToList();
        released.Should().HaveCount(2);
        released.Should().OnlyContain(m => m.RetryCount == 0 && m.LockId == null && m.LockedUntilUtc == null,
            "liberar no consume intentos ni deja bloqueos");
    }
}
