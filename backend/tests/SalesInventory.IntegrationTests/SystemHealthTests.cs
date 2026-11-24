using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Monitoring;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Monitoring;

namespace SalesInventory.IntegrationTests;

/// <summary>Centro de monitoreo operativo (solo Admin) y latido del Worker contra SQL Server real.</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SystemHealthTests(SqlServerConcurrencyDatabaseFixture database)
{
    private const string Path = "/api/admin/system-health";

    private async Task ResetAsync()
    {
        await using var context = database.CreateDbContext();
        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.ProcessedEvents.ExecuteDeleteAsync();
        await context.WorkerHeartbeats.ExecuteDeleteAsync();
    }

    private async Task AuthenticateAsAdminAsync()
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));
    }

    private async Task<SystemHealthDto> GetHealthAsync()
    {
        await AuthenticateAsAdminAsync();
        using var response = await database.Client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SystemHealthDto>())!;
    }

    private async Task<DateTime> SqlNowAsync()
    {
        await using var context = database.CreateDbContext();
        return await context.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync();
    }

    private async Task BeatAsync(WorkerInstanceInfo instance, bool succeeded = true, string? errorType = null)
    {
        await using var context = database.CreateDbContext();
        await new WorkerHeartbeatStore(context).BeatAsync(instance, succeeded, errorType, CancellationToken.None);
    }

    private async Task AgeHeartbeatAsync(Guid instanceId, TimeSpan age)
    {
        var seen = (await SqlNowAsync()) - age;
        await using var context = database.CreateDbContext();
        await context.WorkerHeartbeats.Where(h => h.InstanceId == instanceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(h => h.LastSeenAtUtc, seen));
    }

    private async Task InsertAsync(OutboxMessageStatus status, DateTime occurredAtUtc, DateTime? processedAtUtc = null, DateTime? failedAtUtc = null)
    {
        var message = OutboxMessage.CreatePending(Guid.NewGuid(), "SaleCompleted.v1", "{}", occurredAtUtc);
        message.Status = status;
        message.ProcessedAtUtc = processedAtUtc;
        message.FailedAtUtc = failedAtUtc;
        await using var context = database.CreateDbContext();
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
    }

    // ─── Autorización ───

    [Fact]
    public async Task Endpoint_IsAdminOnly()
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));
        using var seller = await database.Client.GetAsync(Path);
        using var anonymousClient = database.CreateAnonymousClient();
        using var anonymous = await anonymousClient.GetAsync(Path);

        seller.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Worker: sin información / activo / inactivo ───

    [Fact]
    public async Task WithoutHeartbeats_TheWorkerIsNoInformation_NotInactive()
    {
        await ResetAsync();

        var health = await GetHealthAsync();

        health.SqlServer.Status.Should().Be("Available");
        health.SqlServer.LatencyMs.Should().NotBeNull();
        health.Worker.Status.Should().Be("NoInformation");
        health.Worker.Instances.Should().BeEmpty();
        health.Warnings.Should().Contain("worker_no_information");
        health.Status.Should().Be("Degraded");
    }

    [Fact]
    public async Task ARecentHeartbeat_MakesTheWorkerActive_ButProcessingIsOnlyHealthyAfterASuccessfulCycle()
    {
        await ResetAsync();
        var instance = WorkerInstanceInfo.Create("9.9.9-test");

        await BeatAsync(instance, succeeded: false);
        var alive = await GetHealthAsync();
        await BeatAsync(instance, succeeded: true);
        var working = await GetHealthAsync();

        alive.Worker.Status.Should().Be("Active");
        alive.Worker.Instances!.Single().Processing.Should().Be("Unknown", "un latido solo prueba que el proceso vive");
        alive.Worker.Instances!.Single().Version.Should().Be("9.9.9-test");
        working.Worker.Instances!.Single().Processing.Should().Be("Healthy");
        working.Worker.Instances!.Single().LastSuccessfulCycleAtUtc.Should().NotBeNull();
        working.Worker.Processing.Should().Be("Healthy");
    }

    [Fact]
    public async Task AFailingCycle_MarksProcessingDegradedWhileTheHeartbeatIsStillActive()
    {
        await ResetAsync();
        var instance = WorkerInstanceInfo.Create("1.0.0");
        await BeatAsync(instance, succeeded: true);
        await BeatAsync(instance, succeeded: false, errorType: "SqlException");

        var health = await GetHealthAsync();

        health.Worker.Status.Should().Be("Active");
        var worker = health.Worker.Instances!.Single();
        worker.Processing.Should().Be("Degraded");
        worker.LastErrorType.Should().Be("SqlException");
        health.Warnings.Should().Contain("worker_processing_degraded");
    }

    [Fact]
    public async Task AHeartbeatOlderThanTheThreshold_MakesTheInstanceInactive_UsingTheSqlServerClock()
    {
        await ResetAsync();
        var instance = WorkerInstanceInfo.Create("1.0.0");
        await BeatAsync(instance);
        await AgeHeartbeatAsync(instance.InstanceId, TimeSpan.FromMinutes(5));

        var health = await GetHealthAsync();

        health.Worker.Status.Should().Be("Inactive");
        var worker = health.Worker.Instances!.Single();
        worker.Status.Should().Be("Inactive");
        worker.SecondsSinceLastSeen.Should().BeGreaterThanOrEqualTo(295);
        health.Warnings.Should().Contain("worker_inactive");
    }

    [Fact]
    public async Task SeveralInstances_AreReported_AndTheWorkerIsActiveWhileAnyOneBeats()
    {
        await ResetAsync();
        var alive = WorkerInstanceInfo.Create("1.0.0");
        var dead = WorkerInstanceInfo.Create("1.0.0");
        await BeatAsync(alive);
        await BeatAsync(dead);
        await AgeHeartbeatAsync(dead.InstanceId, TimeSpan.FromMinutes(10));

        var health = await GetHealthAsync();

        health.Worker.Status.Should().Be("Active");
        health.Worker.Instances!.Should().HaveCount(2);
        health.Worker.Instances!.Single(i => i.InstanceId == alive.InstanceId).Status.Should().Be("Active");
        health.Worker.Instances!.Single(i => i.InstanceId == dead.InstanceId).Status.Should().Be("Inactive");
    }

    [Fact]
    public async Task RepeatedBeats_UpdateTheSameRow_AndKeepTheOriginalStart()
    {
        await ResetAsync();
        var instance = WorkerInstanceInfo.Create("1.0.0");

        await BeatAsync(instance);
        await using var first = database.CreateDbContext();
        var started = (await first.WorkerHeartbeats.AsNoTracking().SingleAsync()).StartedAtUtc;
        await BeatAsync(instance);
        await BeatAsync(instance);

        await using var check = database.CreateDbContext();
        var row = await check.WorkerHeartbeats.AsNoTracking().SingleAsync();
        row.StartedAtUtc.Should().Be(started);
        row.LastSeenAtUtc.Should().BeOnOrAfter(started);
    }

    // ─── Eventos ───

    [Fact]
    public async Task Events_ReportCountsAndTheAgeOfTheOldestPendingEvent_FromTheSqlClock()
    {
        await ResetAsync();
        var now = await SqlNowAsync();
        await InsertAsync(OutboxMessageStatus.Pending, now.AddMinutes(-10));
        await InsertAsync(OutboxMessageStatus.Pending, now.AddMinutes(-2));
        await InsertAsync(OutboxMessageStatus.Processed, now.AddMinutes(-30), processedAtUtc: now.AddMinutes(-29));

        var health = await GetHealthAsync();

        var events = health.Events!;
        events.Pending.Should().Be(2);
        events.Processed.Should().Be(1);
        events.OldestPendingAgeSeconds.Should().BeInRange(590, 700);
        events.ProcessedLast24Hours.Should().Be(1);
        events.FailedLast24Hours.Should().Be(0);
        health.Warnings.Should().Contain("outbox_backlog_old");
    }

    [Fact]
    public async Task FailedEvents_AreCountedInTheLast24Hours_OnlyWhenTheirFailureWasRecordedRecently()
    {
        await ResetAsync();
        var now = await SqlNowAsync();
        await InsertAsync(OutboxMessageStatus.Failed, now.AddHours(-2), failedAtUtc: now.AddHours(-1));
        await InsertAsync(OutboxMessageStatus.Failed, now.AddDays(-3), failedAtUtc: now.AddDays(-2));

        var health = await GetHealthAsync();

        health.Events!.Failed.Should().Be(2);
        health.Events.FailedLast24Hours.Should().Be(1);
        health.Warnings.Should().Contain("outbox_failures_recent");
    }

    // ─── Serie temporal ───

    [Fact]
    public async Task Series_IsPublishedOnlyWithEnoughPersistedData()
    {
        await ResetAsync();
        var now = await SqlNowAsync();
        var sameInstant = now.AddHours(-3);
        await InsertAsync(OutboxMessageStatus.Processed, sameInstant.AddMinutes(-1), processedAtUtc: sameInstant);
        await InsertAsync(OutboxMessageStatus.Processed, sameInstant.AddMinutes(-1), processedAtUtc: sameInstant);

        var insufficient = await GetHealthAsync();

        insufficient.SeriesStatus.Should().Be("InsufficientData");
        insufficient.Series.Should().BeNull();
        insufficient.Events!.ProcessedLast24Hours.Should().Be(2, "los totales no dependen de la serie");

        await InsertAsync(OutboxMessageStatus.Processed, now.AddHours(-6), processedAtUtc: now.AddHours(-5));
        await InsertAsync(OutboxMessageStatus.Failed, now.AddHours(-5), failedAtUtc: now.AddHours(-1));

        var enough = await GetHealthAsync();

        enough.SeriesStatus.Should().Be("Available");
        enough.Series!.BucketMinutes.Should().Be(60);
        enough.Series.Points.Sum(p => p.Processed).Should().Be(3);
        enough.Series.Points.Sum(p => p.Failed).Should().Be(1);
        enough.Series.Points.Select(p => p.BucketStartUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task TheStore_StampsFailedAtWithTheSqlClockOnlyWhenTheMessageBecomesFailed()
    {
        await ResetAsync();
        await InsertAsync(OutboxMessageStatus.Pending, (await SqlNowAsync()).AddMinutes(-1));
        var options = Microsoft.Extensions.Options.Options.Create(new SalesInventory.Infrastructure.Outbox.OutboxOptions { MaxAttempts = 3 });

        await using var context = database.CreateDbContext();
        var store = new SalesInventory.Infrastructure.Outbox.OutboxStore(context, options);
        var claimed = (await store.ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        await store.RecordFailureAsync(claimed, "boom", permanent: false, TimeSpan.Zero, CancellationToken.None);

        await using (var check = database.CreateDbContext())
        {
            var retried = await check.OutboxMessages.AsNoTracking().SingleAsync();
            retried.Status.Should().Be(OutboxMessageStatus.Pending);
            retried.FailedAtUtc.Should().BeNull();
        }

        var again = (await store.ClaimBatchAsync(Guid.NewGuid(), CancellationToken.None)).Single();
        var before = await SqlNowAsync();
        await store.RecordFailureAsync(again, "boom", permanent: true, TimeSpan.Zero, CancellationToken.None);
        var after = await SqlNowAsync();

        await using var final = database.CreateDbContext();
        var failed = await final.OutboxMessages.AsNoTracking().SingleAsync();
        failed.Status.Should().Be(OutboxMessageStatus.Failed);
        failed.FailedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ─── Reloj del reprocesamiento manual ───

    [Fact]
    public async Task ManualRequeue_UsesTheSqlServerClock_AndClearsTheFailureTimestamp()
    {
        await ResetAsync();
        await AuthenticateAsAdminAsync();
        var failed = OutboxMessage.CreatePending(Guid.NewGuid(), "SaleCompleted.v1", "{}", (await SqlNowAsync()).AddMinutes(-5));
        failed.Status = OutboxMessageStatus.Failed;
        failed.RetryCount = 5;
        failed.FailedAtUtc = (await SqlNowAsync()).AddMinutes(-1);
        await using (var seed = database.CreateDbContext())
        {
            seed.OutboxMessages.Add(failed);
            await seed.SaveChangesAsync();
        }

        var before = await SqlNowAsync();
        using var response = await database.Client.PostAsync($"/api/outbox/messages/{failed.Id}/requeue", null);
        var after = await SqlNowAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var check = database.CreateDbContext();
        var row = await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == failed.Id);
        row.Status.Should().Be(OutboxMessageStatus.Pending);
        row.NextAttemptAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        row.FailedAtUtc.Should().BeNull();
    }

    // ─── Seguridad de la respuesta ───

    [Fact]
    public async Task Response_NeverExposesConnectionStringsSecretsOrExceptions()
    {
        await ResetAsync();
        await BeatAsync(WorkerInstanceInfo.Create("1.0.0"), succeeded: false, errorType: "InvalidOperationException");
        await AuthenticateAsAdminAsync();

        using var response = await database.Client.GetAsync(Path);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContainEquivalentOf("Server=").And.NotContainEquivalentOf("Password")
            .And.NotContainEquivalentOf("Data Source").And.NotContainEquivalentOf("StackTrace")
            .And.NotContainEquivalentOf(database.ConnectionString);
    }
}
