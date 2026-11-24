using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SalesInventory.Application.Monitoring;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Monitoring;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class WorkerHealthEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(45);

    private static WorkerHeartbeat Beat(
        int secondsAgo,
        DateTime? lastSuccess = null,
        string? error = null,
        Guid? id = null) => new()
    {
        InstanceId = id ?? Guid.NewGuid(),
        Version = "1.0.0",
        StartedAtUtc = Now.AddHours(-1),
        LastSeenAtUtc = Now.AddSeconds(-secondsAgo),
        LastSuccessfulCycleAtUtc = lastSuccess,
        LastCycleErrorType = error
    };

    [Fact]
    public void NoRows_IsNoInformation_NeverInactive()
    {
        var result = WorkerHealthEvaluator.Evaluate([], Now, Threshold);

        result.Status.Should().Be(HealthStates.NoInformation);
        result.Processing.Should().Be(HealthStates.Unknown);
        result.Instances.Should().BeEmpty();
        result.HeartbeatThresholdSeconds.Should().Be(45);
    }

    [Fact]
    public void RecentHeartbeatWithRecentSuccessfulCycle_IsActiveAndHealthy()
    {
        var result = WorkerHealthEvaluator.Evaluate([Beat(5, Now.AddSeconds(-5))], Now, Threshold);

        result.Status.Should().Be(HealthStates.Active);
        result.Processing.Should().Be(HealthStates.Healthy);
    }

    [Fact]
    public void RecentHeartbeatWithoutAnyCompletedCycle_IsActiveButProcessingIsUnknown()
    {
        var result = WorkerHealthEvaluator.Evaluate([Beat(5)], Now, Threshold);

        result.Status.Should().Be(HealthStates.Active);
        result.Processing.Should().Be(HealthStates.Unknown, "un latido no garantiza el procesamiento");
    }

    [Fact]
    public void ALastCycleError_MakesProcessingDegraded_EvenWithAFreshHeartbeat()
    {
        var result = WorkerHealthEvaluator.Evaluate([Beat(2, Now.AddSeconds(-2), error: "SqlException")], Now, Threshold);

        result.Status.Should().Be(HealthStates.Active);
        result.Processing.Should().Be(HealthStates.Degraded);
        result.Instances!.Single().LastErrorType.Should().Be("SqlException");
    }

    [Fact]
    public void AStaleSuccessfulCycle_MakesProcessingDegraded()
    {
        var result = WorkerHealthEvaluator.Evaluate([Beat(2, Now.AddMinutes(-10))], Now, Threshold);

        result.Processing.Should().Be(HealthStates.Degraded);
    }

    [Fact]
    public void HeartbeatExactlyAtTheThreshold_IsStillActive_AndOneSecondMoreIsInactive()
    {
        WorkerHealthEvaluator.Evaluate([Beat(45, Now)], Now, Threshold).Status.Should().Be(HealthStates.Active);

        var stale = WorkerHealthEvaluator.Evaluate([Beat(46, Now)], Now, Threshold);

        stale.Status.Should().Be(HealthStates.Inactive);
        stale.Processing.Should().Be(HealthStates.Unknown);
        stale.Instances!.Single().Status.Should().Be(HealthStates.Inactive);
    }

    [Fact]
    public void SeveralInstances_WorkerIsActiveWhileAnyOneBeats_AndInstancesAreOrderedByActivity()
    {
        var alive = Beat(3, Now.AddSeconds(-3), id: Guid.NewGuid());
        var dead = Beat(600, id: Guid.NewGuid());

        var result = WorkerHealthEvaluator.Evaluate([dead, alive], Now, Threshold);

        result.Status.Should().Be(HealthStates.Active);
        result.Processing.Should().Be(HealthStates.Healthy);
        result.Instances!.Select(i => i.InstanceId).Should().Equal(alive.InstanceId, dead.InstanceId);
    }

    [Fact]
    public void AllInstancesStale_IsInactive()
    {
        WorkerHealthEvaluator.Evaluate([Beat(100), Beat(500)], Now, Threshold).Status.Should().Be(HealthStates.Inactive);
    }

    [Fact]
    public void ClockSkewBetweenHeartbeatAndNow_NeverProducesNegativeAges()
    {
        var result = WorkerHealthEvaluator.Evaluate([Beat(-5, Now)], Now, Threshold);

        result.Instances!.Single().SecondsSinceLastSeen.Should().Be(0);
    }
}

public sealed class WorkerCycleTrackerTests
{
    [Fact]
    public void Take_ReportsSuccessOnceAndResetsIt_ButKeepsTheLastError()
    {
        var tracker = new WorkerCycleTracker();
        tracker.RecordFailure(new InvalidOperationException("secreto"));
        tracker.Take().Should().Be((false, "InvalidOperationException"), "solo el nombre del tipo, nunca el mensaje");

        tracker.RecordSuccess();
        tracker.Take().Should().Be((true, (string?)null));
        tracker.Take().Should().Be((false, (string?)null));
    }

    [Fact]
    public void KeepSuccess_RestoresTheSuccessFlagWithoutTouchingTheError()
    {
        var tracker = new WorkerCycleTracker();
        tracker.RecordSuccess();
        tracker.Take();
        tracker.RecordFailure(new TimeoutException());

        tracker.KeepSuccess();

        tracker.Take().Should().Be((true, "TimeoutException"));
    }
}

public sealed class SystemHealthOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var act = () => new SystemHealthOptions().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void AThresholdNotAboveTheHeartbeatInterval_IsRejected()
    {
        var options = new SystemHealthOptions { HeartbeatInterval = TimeSpan.FromSeconds(30), HeartbeatThreshold = TimeSpan.FromSeconds(30) };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*HeartbeatThreshold*");
    }
}

public sealed class SystemHealthServiceTests
{
    [Fact]
    public async Task WhenSqlServerDoesNotRespond_TheStateIsDegradedWithUnavailableSectionsAndNeverFakeZeros()
    {
        // InMemory no admite SQL directo: equivale a un SQL Server que no responde.
        await using var context = new SalesInventoryDbContext(
            new DbContextOptionsBuilder<SalesInventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new SystemHealthService(
            context, Mock.Of<IOutboxMonitorService>(), Options.Create(new SystemHealthOptions()), NullLogger<SystemHealthService>.Instance);

        var health = await service.GetAsync();

        health.Status.Should().Be(HealthStates.Degraded);
        health.Api.Status.Should().Be(HealthStates.Up);
        health.SqlServer.Should().Be(new SqlServerHealthDto(HealthStates.Unavailable, null));
        health.Worker.Status.Should().Be(HealthStates.Unavailable);
        health.Worker.Instances.Should().BeNull();
        health.Events.Should().BeNull("sin datos no hay ceros ficticios");
        health.SeriesStatus.Should().Be(HealthStates.Unavailable);
        health.Series.Should().BeNull();
        health.GeneratedAtUtc.Should().BeNull();
        health.Warnings.Should().Equal("sql_unavailable");
    }

    [Fact]
    public async Task WhenSqlServerDoesNotRespond_NothingInternalLeaksInTheResponse()
    {
        await using var context = new SalesInventoryDbContext(
            new DbContextOptionsBuilder<SalesInventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new SystemHealthService(
            context, Mock.Of<IOutboxMonitorService>(), Options.Create(new SystemHealthOptions()), NullLogger<SystemHealthService>.Instance);

        var json = JsonSerializer.Serialize(await service.GetAsync());

        json.Should().NotContainEquivalentOf("Exception").And.NotContainEquivalentOf("Server=")
            .And.NotContainEquivalentOf("relacional").And.NotContainEquivalentOf("Relational");
    }

    [Fact]
    public async Task ACancelledRequest_PropagatesTheCancellation_InsteadOfBeingReportedAsDegraded()
    {
        await using var context = new SalesInventoryDbContext(
            new DbContextOptionsBuilder<SalesInventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new SystemHealthService(
            context, Mock.Of<IOutboxMonitorService>(), Options.Create(new SystemHealthOptions()), NullLogger<SystemHealthService>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await service.GetAsync(cancelled.Token);

        // El probe de SQL falla (InMemory) con la solicitud ya cancelada: no se disfraza de degradación.
        await act.Should().ThrowAsync<Exception>();
    }
}
