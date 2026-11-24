using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Sales.Events;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using ValidationException = SalesInventory.Application.Common.Exceptions.ValidationException;

namespace SalesInventory.Application.Outbox;

public sealed record OutboxSummaryDto(
    int Pending,
    int Processing,
    int Processed,
    int Failed,
    int ExpiredLeases,
    DateTime? OldestPendingAtUtc);

public sealed record OutboxMessageListItemDto(
    Guid Id,
    string EventType,
    string Status,
    DateTime OccurredAtUtc,
    DateTime? ProcessedAtUtc,
    int RetryCount,
    DateTime NextAttemptAtUtc,
    string? LastError);

public sealed record OutboxConsumerRecordDto(string Consumer, DateTime ProcessedAtUtc);

/// <summary>
/// Información técnica SEGURA de un evento: sin payload, sin LockId, sin excepciones completas. Solo identificadores
/// de negocio no sensibles (SaleId, número de líneas) y un error ya saneado y truncado.
/// </summary>
public sealed record OutboxMessageDetailDto(
    Guid Id,
    string EventType,
    string Status,
    DateTime OccurredAtUtc,
    DateTime? ProcessedAtUtc,
    int RetryCount,
    DateTime NextAttemptAtUtc,
    string? LastError,
    DateTime? LockedUntilUtc,
    bool LeaseExpired,
    int PayloadBytes,
    int? SaleId,
    int? ItemCount,
    IReadOnlyList<OutboxConsumerRecordDto> Consumers);

public class OutboxFilterParams
{
    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    /// <summary>Pending, Processing, Processed o Failed.</summary>
    public string? Status { get; set; }

    /// <summary>Tipo de evento exacto, p. ej. SaleCompleted.v1.</summary>
    public string? EventType { get; set; }

    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public interface IOutboxMonitorService
{
    Task<OutboxSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<OutboxMessageListItemDto>> GetPagedAsync(OutboxFilterParams filter, CancellationToken cancellationToken = default);
    Task<OutboxMessageDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task RequeueFailedAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Todos los instantes de comparación y escritura usan el reloj de SQL Server: en LINQ, <c>DateTime.UtcNow</c> se traduce a
/// <c>SYSUTCDATETIME()</c> y se evalúa en el servidor (el mismo reloj que usa el procesador del Outbox), no el de la API.
/// </summary>
public sealed class OutboxMonitorService(IApplicationDbContext context) : IOutboxMonitorService
{
    private const int ListErrorLength = 300;

    public async Task<OutboxSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var counts = await context.OutboxMessages
            .AsNoTracking()
            .GroupBy(message => message.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(OutboxMessageStatus status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        var expiredLeases = await context.OutboxMessages
            .AsNoTracking()
            .CountAsync(m => m.Status == OutboxMessageStatus.Processing && m.LockedUntilUtc <= DateTime.UtcNow, cancellationToken);

        var oldestPending = await context.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => (DateTime?)m.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new OutboxSummaryDto(
            CountOf(OutboxMessageStatus.Pending),
            CountOf(OutboxMessageStatus.Processing),
            CountOf(OutboxMessageStatus.Processed),
            CountOf(OutboxMessageStatus.Failed),
            expiredLeases,
            oldestPending is null ? null : Utc(oldestPending.Value));
    }

    public async Task<PagedResult<OutboxMessageListItemDto>> GetPagedAsync(
        OutboxFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = context.OutboxMessages.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<OutboxMessageStatus>(filter.Status, ignoreCase: true, out var status)
                || !Enum.IsDefined(status))
            {
                throw new ValidationException("El estado debe ser Pending, Processing, Processed o Failed.");
            }

            query = query.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            var eventType = filter.EventType.Trim();
            query = query.Where(m => m.EventType == eventType);
        }

        if (filter.From.HasValue)
            query = query.Where(m => m.OccurredAtUtc >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(m => m.OccurredAtUtc <= filter.To.Value);

        var total = await query.CountAsync(cancellationToken);

        // Se proyectan solo columnas seguras: el payload no se lee.
        var rows = await query
            .OrderByDescending(m => m.OccurredAtUtc)
            .ThenBy(m => m.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(m => new
            {
                m.Id, m.EventType, m.Status, m.OccurredAtUtc, m.ProcessedAtUtc, m.RetryCount, m.NextAttemptAtUtc, m.LastError
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(m => new OutboxMessageListItemDto(
            m.Id,
            m.EventType,
            m.Status.ToString(),
            Utc(m.OccurredAtUtc),
            m.ProcessedAtUtc is null ? null : Utc(m.ProcessedAtUtc.Value),
            m.RetryCount,
            Utc(m.NextAttemptAtUtc),
            SafeError(m.LastError, ListErrorLength))).ToList();

        return new PagedResult<OutboxMessageListItemDto>(items, total, filter.PageNumber, filter.PageSize);
    }

    public async Task<OutboxMessageDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await context.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(OutboxMessage), id);

        var consumers = (await context.ProcessedEvents
                .AsNoTracking()
                .Where(p => p.EventId == id)
                .OrderBy(p => p.ProcessedAtUtc)
                .Select(p => new { p.Consumer, p.ProcessedAtUtc })
                .ToListAsync(cancellationToken))
            .Select(p => new OutboxConsumerRecordDto(p.Consumer, Utc(p.ProcessedAtUtc)))
            .ToList();

        var (saleId, itemCount) = SafeSaleSummary(message);
        var leaseExpired = message.Status == OutboxMessageStatus.Processing
            && await context.OutboxMessages
                .AsNoTracking()
                .AnyAsync(m => m.Id == id && m.LockedUntilUtc <= DateTime.UtcNow, cancellationToken);

        return new OutboxMessageDetailDto(
            message.Id,
            message.EventType,
            message.Status.ToString(),
            Utc(message.OccurredAtUtc),
            message.ProcessedAtUtc is null ? null : Utc(message.ProcessedAtUtc.Value),
            message.RetryCount,
            Utc(message.NextAttemptAtUtc),
            SafeError(message.LastError, OutboxErrorFormatter.MaxLength),
            message.LockedUntilUtc is null ? null : Utc(message.LockedUntilUtc.Value),
            leaseExpired,
            Encoding.UTF8.GetByteCount(message.Payload),
            saleId,
            itemCount,
            consumers);
    }

    /// <summary>
    /// Reprocesamiento manual: transición atómica Failed → Pending condicionada al estado en la propia sentencia
    /// UPDATE, así que de varias solicitudes simultáneas solo una la aplica. No toca EventId ni ProcessedEvents
    /// (los consumidores ya procesados siguen siendo idempotentes frente a la nueva entrega).
    /// </summary>
    public async Task RequeueFailedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // NextAttemptAtUtc con el reloj de SQL Server (SYSUTCDATETIME()), el mismo que usa el procesador para decidir qué está listo.
        var updated = await context.OutboxMessages
            .Where(m => m.Id == id && m.Status == OutboxMessageStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Status, OutboxMessageStatus.Pending)
                .SetProperty(m => m.RetryCount, 0)
                .SetProperty(m => m.NextAttemptAtUtc, m => DateTime.UtcNow)
                .SetProperty(m => m.FailedAtUtc, (DateTime?)null)
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.ProcessedAtUtc, (DateTime?)null)
                .SetProperty(m => m.LockId, (Guid?)null)
                .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), cancellationToken);

        if (updated > 0)
            return;

        var exists = await context.OutboxMessages.AsNoTracking().AnyAsync(m => m.Id == id, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(OutboxMessage), id);

        throw new ConflictException("Solo se pueden reprocesar eventos en estado Failed.");
    }

    private static string? SafeError(string? error, int maxLength) =>
        string.IsNullOrWhiteSpace(error) ? null : OutboxErrorFormatter.Sanitize(error, maxLength);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Extrae solo identificadores de negocio no sensibles de SaleCompleted.v1; nunca devuelve el payload.</summary>
    private static (int? SaleId, int? ItemCount) SafeSaleSummary(OutboxMessage message)
    {
        if (message.EventType != SaleCompletedV1.EventType)
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(message.Payload);
            var root = document.RootElement;
            int? saleId = root.TryGetProperty("saleId", out var sale) && sale.TryGetInt32(out var id) ? id : null;
            int? itemCount = root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength()
                : null;
            return (saleId, itemCount);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
