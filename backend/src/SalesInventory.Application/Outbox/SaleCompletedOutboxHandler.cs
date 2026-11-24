using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Outbox;

/// <summary>Valida y deserializa el contrato <c>SaleCompleted.v1</c>; cualquier defecto es un error permanente.</summary>
public static class SaleCompletedEventParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static SaleCompletedV1 Parse(OutboxEnvelope message)
    {
        SaleCompletedV1? contract;
        try
        {
            contract = JsonSerializer.Deserialize<SaleCompletedV1>(message.Payload, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new PermanentOutboxException("El payload del evento no es un SaleCompleted.v1 válido.", exception);
        }

        if (contract is null)
            throw new PermanentOutboxException("El payload del evento está vacío.");

        if (contract.SchemaVersion != SaleCompletedV1.CurrentSchemaVersion)
            throw new PermanentOutboxException(
                $"Versión de esquema no soportada para SaleCompleted: {contract.SchemaVersion}.");

        if (contract.EventId != message.EventId)
            throw new PermanentOutboxException("El EventId del payload no coincide con el del mensaje.");

        if (contract.SaleId <= 0 || contract.UserId <= 0 || contract.Items is null || contract.Items.Count == 0)
            throw new PermanentOutboxException("El payload de SaleCompleted.v1 está incompleto.");

        return contract;
    }
}

/// <summary>
/// Consumidor de <c>SaleCompleted.v1</c>. Su efecto es local a SQL Server: registra de forma persistente que el
/// evento fue procesado (único por EventId y consumidor). Es idempotente: una entrega repetida no duplica el efecto.
/// </summary>
public sealed class SaleCompletedOutboxHandler(IApplicationDbContext context, TimeProvider clock) : IOutboxEventHandler
{
    public const string Consumer = "sales-inventory.sale-completed-registry";

    public string EventType => SaleCompletedV1.EventType;

    public string ConsumerName => Consumer;

    public async Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        var contract = SaleCompletedEventParser.Parse(message);

        var alreadyProcessed = await context.ProcessedEvents.AnyAsync(
            processed => processed.EventId == message.EventId && processed.Consumer == Consumer,
            cancellationToken);
        if (alreadyProcessed)
        {
            return;
        }

        var units = contract.Items.Sum(item => item.Quantity);
        context.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = message.EventId,
            Consumer = Consumer,
            EventType = message.EventType,
            ProcessedAtUtc = clock.GetUtcNow().UtcDateTime,
            Summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Venta {contract.SaleId}: {contract.Items.Count} producto(s), {units} unidad(es), total {contract.Total}")
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
