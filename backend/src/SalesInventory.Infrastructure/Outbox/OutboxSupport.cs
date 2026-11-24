using Microsoft.Extensions.Options;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Outbox;

/// <summary>Mensaje reclamado por un worker: instantánea con el lease y la RowVersion vigentes al reclamar.</summary>
public sealed record ClaimedOutboxMessage(
    Guid Id,
    string EventType,
    string Payload,
    DateTime OccurredAtUtc,
    int RetryCount,
    Guid LockId,
    byte[] RowVersion,
    string? TraceParent = null,
    string? TraceState = null)
{
    public static ClaimedOutboxMessage From(OutboxMessage message) => new(
        message.Id,
        message.EventType,
        message.Payload,
        message.OccurredAtUtc,
        message.RetryCount,
        message.LockId ?? throw new InvalidOperationException("El mensaje reclamado no tiene LockId."),
        message.RowVersion,
        message.TraceParent,
        message.TraceState);
}

/// <summary>El worker ya no es dueño del mensaje (lease vencido y reclamado por otro): su trabajo se descarta.</summary>
public sealed class LeaseLostException(Guid messageId)
    : Exception($"El mensaje {messageId} ya no pertenece a este worker (lease perdido).");

/// <summary>Backoff exponencial con jitter: delay = base · 2^(intento−1), acotado, multiplicado por un factor en [0.5, 1].</summary>
public sealed class OutboxBackoffCalculator
{
    private readonly OutboxOptions _options;
    private readonly Func<double> _random;

    public OutboxBackoffCalculator(IOptions<OutboxOptions> options, Func<double>? random = null)
    {
        _options = options.Value;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <param name="failedAttempts">Intentos ya consumidos (≥ 1).</param>
    public TimeSpan Next(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 30);
        var seconds = _options.BaseRetryDelay.TotalSeconds * Math.Pow(2, exponent);
        var capped = Math.Min(seconds, _options.MaxRetryDelay.TotalSeconds);
        var jitter = 0.5 + (0.5 * Math.Clamp(_random(), 0d, 1d));
        return TimeSpan.FromSeconds(capped * jitter);
    }
}
