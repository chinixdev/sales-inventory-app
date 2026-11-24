using System.Text.Json;
using SalesInventory.Application.Observability;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Sales.Events;

/// <summary>
/// Contrato público y versionado del evento «venta completada» (versión 1).
/// Es un DTO explícito: no expone entidades EF. Un cambio incompatible exige un nuevo tipo (V2).
/// </summary>
public sealed record SaleCompletedV1(
    int SchemaVersion,
    Guid EventId,
    int SaleId,
    int UserId,
    decimal Total,
    DateTime OccurredAtUtc,
    IReadOnlyList<SaleCompletedItemV1> Items)
{
    public const string EventType = "SaleCompleted.v1";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Construye el mensaje de Outbox para una venta ya persistida (con Id) y sus detalles.</summary>
    public static OutboxMessage ToOutboxMessage(Sale sale, IEnumerable<SaleDetail> details)
    {
        var eventId = Guid.NewGuid();
        var occurredAtUtc = DateTime.SpecifyKind(sale.SaleDate, DateTimeKind.Utc);
        var contract = new SaleCompletedV1(
            CurrentSchemaVersion,
            eventId,
            sale.Id,
            sale.UserId,
            sale.Total,
            occurredAtUtc,
            details
                .OrderBy(detail => detail.ProductId)
                .Select(detail => new SaleCompletedItemV1(detail.ProductId, detail.Quantity))
                .ToList());

        // Contexto de la operación original (si hay traza activa) para enlazar el procesamiento asíncrono.
        var (traceParent, traceState) = TraceContextPropagation.Capture();

        return OutboxMessage.CreatePending(
            eventId,
            EventType,
            JsonSerializer.Serialize(contract, SerializerOptions),
            occurredAtUtc,
            traceParent,
            traceState);
    }
}

public sealed record SaleCompletedItemV1(int ProductId, int Quantity);
