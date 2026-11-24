using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Observability;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.Application.Outbox;

/// <summary>
/// Consumidor de <c>SaleCompleted.v1</c> que genera notificaciones persistentes de stock bajo o agotado para los
/// administradores activos. Evalúa el stock VIGENTE y el umbral del producto al procesar (no el del momento de la
/// venta), de modo que una alerta de un producto ya repuesto no se emite. Las notificaciones, el registro de
/// ProcessedEvents y la finalización del mensaje se confirman en una única transacción.
/// </summary>
public sealed class SaleStockAlertsOutboxHandler(
    IApplicationDbContext context,
    TimeProvider clock,
    DeferredMetrics? deferredMetrics = null) : IOutboxEventHandler
{
    public const string Consumer = "sales-inventory.stock-alerts";

    public string EventType => SaleCompletedV1.EventType;

    public string ConsumerName => Consumer;

    public async Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.Source.StartActivity("notifications.generate");
        activity?.SetTag("messaging.consumer", Consumer);
        var contract = SaleCompletedEventParser.Parse(message);

        // Entrega repetida: el efecto ya se aplicó, no se duplica nada.
        if (await context.ProcessedEvents.AnyAsync(
                processed => processed.EventId == message.EventId && processed.Consumer == Consumer,
                cancellationToken))
        {
            activity?.SetTag("notifications.duplicate_delivery", true);
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var productIds = contract.Items.Select(item => item.ProductId).Distinct().ToList();

        // Stock y umbral actuales (no los de la venta): evita alertas obsoletas.
        var alertingProducts = await context.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id) && product.IsActive && product.Stock <= product.MinimumStock)
            .Select(product => new { product.Id, product.Name, product.SKU, product.Stock, product.MinimumStock })
            .ToListAsync(cancellationToken);

        var created = 0;
        var byType = new Dictionary<NotificationType, int>();
        if (alertingProducts.Count > 0)
        {
            var adminIds = await context.Users
                .AsNoTracking()
                .Where(user => user.IsActive && user.Role == UserRole.Admin)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

            // Ya hay una alerta sin leer del mismo tipo para ese producto y usuario: no se acumulan duplicados.
            var alertingIds = alertingProducts.Select(product => product.Id).ToList();
            var unread = (await context.Notifications
                    .AsNoTracking()
                    .Where(n => n.ReadAtUtc == null && adminIds.Contains(n.UserId) && alertingIds.Contains(n.ProductId))
                    .Select(n => new { n.UserId, n.ProductId, n.Type })
                    .ToListAsync(cancellationToken))
                .Select(n => (n.UserId, n.ProductId, n.Type))
                .ToHashSet();

            foreach (var product in alertingProducts)
            {
                var type = product.Stock <= 0 ? NotificationType.OutOfStock : NotificationType.LowStock;
                var (title, text) = Describe(type, product.Name, product.SKU, product.Stock, product.MinimumStock);

                foreach (var adminId in adminIds)
                {
                    if (unread.Contains((adminId, product.Id, type)))
                        continue;

                    context.Notifications.Add(new Notification
                    {
                        UserId = adminId,
                        Type = type,
                        ProductId = product.Id,
                        Title = title,
                        Message = text,
                        CurrentStock = product.Stock,
                        MinimumStock = product.MinimumStock,
                        SourceEventId = message.EventId,
                        CreatedAtUtc = now
                    });
                    created++;
                    byType[type] = byType.GetValueOrDefault(type) + 1;
                }
            }
        }

        context.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = message.EventId,
            Consumer = Consumer,
            EventType = message.EventType,
            ProcessedAtUtc = now,
            Summary = string.Create(CultureInfo.InvariantCulture, $"Venta {contract.SaleId}: {created} notificación(es) de stock")
        });

        await context.SaveChangesAsync(cancellationToken);
        activity?.SetTag("notifications.created", created);

        // Métrica diferida: solo se contabiliza si la transacción del mensaje se confirma (no por reintentos internos).
        if (deferredMetrics is not null && byType.Count > 0)
        {
            var counts = byType.ToArray();
            deferredMetrics.Add(() =>
            {
                foreach (var (type, count) in counts)
                {
                    AppTelemetry.NotificationsGenerated.Add(count, new KeyValuePair<string, object?>("type", type.ToString()));
                }
            });
        }
    }

    private static (string Title, string Message) Describe(NotificationType type, string name, string sku, int stock, int minimum)
    {
        var product = $"{name} ({sku})";
        return type == NotificationType.OutOfStock
            ? ("Producto agotado", Truncate($"{product} se quedó sin existencias."))
            : ("Stock bajo", Truncate($"{product} tiene {stock} unidad(es); el mínimo configurado es {minimum}."));
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
