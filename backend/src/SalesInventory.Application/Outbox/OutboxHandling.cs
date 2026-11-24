namespace SalesInventory.Application.Outbox;

/// <summary>Mensaje del Outbox entregado a un handler (sin detalles de bloqueo ni de infraestructura).</summary>
public sealed record OutboxEnvelope(Guid EventId, string EventType, string Payload, DateTime OccurredAtUtc);

/// <summary>
/// Consumidor de un tipo de evento versionado (p. ej. <c>SaleCompleted.v1</c>). La entrega es at-least-once:
/// el handler DEBE ser idempotente. Si su efecto es local a SQL Server, debe persistirlo con el mismo
/// <c>IApplicationDbContext</c> del scope, que participa en la transacción que también finaliza el mensaje.
/// Un mismo evento puede tener varios consumidores; cada uno se identifica por <see cref="ConsumerName"/>.
/// </summary>
public interface IOutboxEventHandler
{
    string EventType { get; }

    /// <summary>Nombre estable del consumidor (clave de idempotencia junto con el EventId en ProcessedEvents).</summary>
    string ConsumerName { get; }

    Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken);
}

/// <summary>Error no recuperable (payload inválido, versión o evento desconocido): reintentar no lo arreglará.</summary>
public sealed class PermanentOutboxException : Exception
{
    public PermanentOutboxException(string message) : base(message)
    {
    }

    public PermanentOutboxException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Enruta un mensaje a TODOS los consumidores registrados para su <c>EventType</c> exacto y versionado.
/// Se ejecutan en orden dentro de la misma unidad de trabajo, así que o se confirman todos o ninguno.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly Dictionary<string, IOutboxEventHandler[]> _handlers;

    public OutboxDispatcher(IEnumerable<IOutboxEventHandler> handlers)
    {
        var all = handlers.ToList();

        // Dos consumidores con el mismo nombre para el mismo evento colisionarían en ProcessedEvents:
        // es un error de configuración y debe fallar al construir, no en producción.
        var duplicate = all
            .GroupBy(handler => (handler.EventType, handler.ConsumerName))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"El consumidor '{duplicate.Key.ConsumerName}' está registrado más de una vez para '{duplicate.Key.EventType}'.");
        }

        _handlers = all
            .GroupBy(handler => handler.EventType, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(handler => handler.ConsumerName, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    public async Task DispatchAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(message.EventType, out var handlers))
        {
            var shown = message.EventType.Length > 100 ? message.EventType[..100] : message.EventType;
            throw new PermanentOutboxException($"No hay un handler registrado para el evento '{shown}'.");
        }

        foreach (var handler in handlers)
        {
            await handler.HandleAsync(message, cancellationToken);
        }
    }
}
