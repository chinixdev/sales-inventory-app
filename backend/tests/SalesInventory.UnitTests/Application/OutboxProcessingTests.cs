using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Outbox;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Outbox;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class OutboxOptionsTests
{
    [Fact]
    public void DefaultOptions_AreValid() => new OutboxOptions().Invoking(o => o.Validate()).Should().NotThrow();

    [Theory]
    [InlineData(nameof(OutboxOptions.BatchSize), 0)]
    [InlineData(nameof(OutboxOptions.BatchSize), 501)]
    [InlineData(nameof(OutboxOptions.MaxAttempts), 0)]
    [InlineData(nameof(OutboxOptions.MaxAttempts), 51)]
    public void OutOfRangeNumbers_AreRejected(string property, int value)
    {
        var options = new OutboxOptions();
        typeof(OutboxOptions).GetProperty(property)!.SetValue(options, value);

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage($"*{property}*");
    }

    [Fact]
    public void HandlerTimeout_MustBeShorterThanTheLease()
    {
        var options = new OutboxOptions { HandlerTimeout = TimeSpan.FromSeconds(60), LeaseDuration = TimeSpan.FromSeconds(60) };

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*HandlerTimeout*");
    }

    [Fact]
    public void MaxRetryDelay_CannotBeBelowBaseRetryDelay()
    {
        var options = new OutboxOptions { BaseRetryDelay = TimeSpan.FromSeconds(30), MaxRetryDelay = TimeSpan.FromSeconds(10) };

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*MaxRetryDelay*");
    }

    [Fact]
    public void NonPositiveDurations_AreRejected()
    {
        new OutboxOptions { PollingInterval = TimeSpan.Zero }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
        new OutboxOptions { BaseRetryDelay = TimeSpan.Zero }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
        new OutboxOptions { HandlerTimeout = TimeSpan.Zero }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>();
    }
}

public sealed class OutboxBackoffCalculatorTests
{
    private static OutboxBackoffCalculator Create(double jitter, Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions { BaseRetryDelay = TimeSpan.FromSeconds(5), MaxRetryDelay = TimeSpan.FromSeconds(60) };
        configure?.Invoke(options);
        return new OutboxBackoffCalculator(Options.Create(options), () => jitter);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]   // acotado por MaxRetryDelay
    [InlineData(50, 60)]  // sin desbordamiento con intentos enormes
    public void Delay_GrowsExponentiallyUpToTheCap(int attempt, double expectedSeconds) =>
        Create(jitter: 1.0).Next(attempt).TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);

    [Fact]
    public void Jitter_ScalesTheDelayBetweenHalfAndFull()
    {
        Create(jitter: 0.0).Next(3).TotalSeconds.Should().BeApproximately(10, 0.001);
        Create(jitter: 1.0).Next(3).TotalSeconds.Should().BeApproximately(20, 0.001);
        Create(jitter: 0.5).Next(3).TotalSeconds.Should().BeApproximately(15, 0.001);
    }

    [Fact]
    public void RealJitter_StaysWithinBounds()
    {
        var calculator = new OutboxBackoffCalculator(Options.Create(new OutboxOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(4),
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        }));

        for (var i = 0; i < 200; i++)
        {
            var delay = calculator.Next(2).TotalSeconds; // base · 2 = 8 s
            delay.Should().BeInRange(4, 8);
        }
    }
}

public sealed class OutboxErrorFormatterTests
{
    [Fact]
    public void Secrets_AreRedacted()
    {
        var text = OutboxErrorFormatter.Format(new InvalidOperationException(
            "Cadena Server=x;Password=Sup3rS3cret;Pwd: abc123 y token=eyJhbGciOi"));

        text.Should().NotContain("Sup3rS3cret").And.NotContain("abc123").And.NotContain("eyJhbGciOi");
        text.Should().Contain("[redacted]").And.StartWith("InvalidOperationException:");
    }

    [Fact]
    public void LongErrors_AreTruncated_AndFlattenedToOneLine()
    {
        var text = OutboxErrorFormatter.Format(new Exception(new string('x', 5000) + "\nlinea 2"));

        text.Length.Should().BeLessThanOrEqualTo(OutboxErrorFormatter.MaxLength);
        text.Should().NotContain("\n");
    }
}

public sealed class SaleCompletedOutboxHandlerTests
{
    private static SalesInventoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OutboxEnvelope ValidEnvelope(out OutboxMessage message)
    {
        var sale = new Sale { Id = 7, UserId = 3, Total = 150m, SaleDate = DateTime.UtcNow };
        var details = new[]
        {
            new SaleDetail { ProductId = 2, Quantity = 1, UnitPrice = 50m },
            new SaleDetail { ProductId = 1, Quantity = 4, UnitPrice = 25m }
        };
        message = SaleCompletedV1.ToOutboxMessage(sale, details);
        return new OutboxEnvelope(message.Id, message.EventType, message.Payload, message.OccurredAtUtc);
    }

    [Fact]
    public async Task ValidEvent_PersistsOneProcessingRecord()
    {
        await using var context = CreateContext();
        var handler = new SaleCompletedOutboxHandler(context, TimeProvider.System);
        var envelope = ValidEnvelope(out _);

        await handler.HandleAsync(envelope, CancellationToken.None);

        var record = await context.ProcessedEvents.SingleAsync();
        record.EventId.Should().Be(envelope.EventId);
        record.Consumer.Should().Be(SaleCompletedOutboxHandler.Consumer);
        record.EventType.Should().Be("SaleCompleted.v1");
        record.Summary.Should().Contain("Venta 7").And.Contain("2 producto(s)").And.Contain("5 unidad(es)");
    }

    [Fact]
    public async Task RepeatedDelivery_IsIdempotent()
    {
        await using var context = CreateContext();
        var handler = new SaleCompletedOutboxHandler(context, TimeProvider.System);
        var envelope = ValidEnvelope(out _);

        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);
        await handler.HandleAsync(envelope, CancellationToken.None);

        (await context.ProcessedEvents.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"foo\":1}")]
    public async Task InvalidPayload_IsAPermanentError_AndPersistsNothing(string payload)
    {
        await using var context = CreateContext();
        var handler = new SaleCompletedOutboxHandler(context, TimeProvider.System);
        var envelope = new OutboxEnvelope(Guid.NewGuid(), "SaleCompleted.v1", payload, DateTime.UtcNow);

        var act = async () => await handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<PermanentOutboxException>();
        (await context.ProcessedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EventIdMismatch_IsAPermanentError()
    {
        await using var context = CreateContext();
        var handler = new SaleCompletedOutboxHandler(context, TimeProvider.System);
        var envelope = ValidEnvelope(out _) with { EventId = Guid.NewGuid() };

        var act = async () => await handler.HandleAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<PermanentOutboxException>().WithMessage("*EventId*");
    }

    [Fact]
    public async Task Dispatcher_RoutesByExactVersionedEventType_AndRejectsUnknownOnes()
    {
        await using var context = CreateContext();
        var dispatcher = new OutboxDispatcher([new SaleCompletedOutboxHandler(context, TimeProvider.System)]);
        var known = ValidEnvelope(out _);

        await dispatcher.DispatchAsync(known, CancellationToken.None);
        (await context.ProcessedEvents.CountAsync()).Should().Be(1);

        var unknownType = async () => await dispatcher.DispatchAsync(known with { EventType = "SaleCompleted.v2" }, CancellationToken.None);
        await unknownType.Should().ThrowAsync<PermanentOutboxException>().WithMessage("*handler*");
    }

    [Fact]
    public void Dispatcher_FailsFastWhenTwoHandlersClaimTheSameEventType()
    {
        using var context = CreateContext();
        var act = () => new OutboxDispatcher([
            new SaleCompletedOutboxHandler(context, TimeProvider.System),
            new SaleCompletedOutboxHandler(context, TimeProvider.System)]);

        act.Should().Throw<ArgumentException>();
    }
}
