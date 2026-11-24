using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Domain.Entities;
using ValidationException = SalesInventory.Application.Common.Exceptions.ValidationException;

namespace SalesInventory.Application.Audit;

public sealed record AuditLogListItemDto(
    long Id,
    string EntityName,
    string EntityId,
    string Action,
    int? UserId,
    string ActorType,
    string Source,
    IReadOnlyList<string> ChangedFields,
    DateTime OccurredAtUtc,
    string CorrelationId);

/// <summary>Detalle seguro: los valores se vuelven a filtrar contra la lista blanca antes de devolverse.</summary>
public sealed record AuditLogDetailDto(
    long Id,
    string EntityName,
    string EntityId,
    string Action,
    int? UserId,
    string ActorType,
    string Source,
    IReadOnlyList<string> ChangedFields,
    DateTime OccurredAtUtc,
    string CorrelationId,
    JsonElement? OldValues,
    JsonElement? NewValues);

public class AuditLogFilterParams
{
    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    /// <summary>Products, Categories, Sales, SaleDetails, StockMovements o Users.</summary>
    public string? EntityName { get; set; }

    public string? EntityId { get; set; }

    /// <summary>INSERT, UPDATE o DELETE.</summary>
    public string? Action { get; set; }

    public int? UserId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Solo lectura: no existe ningún método para crear, editar ni eliminar registros de auditoría.</summary>
public interface IAuditLogService
{
    Task<PagedResult<AuditLogListItemDto>> GetPagedAsync(AuditLogFilterParams filter, CancellationToken cancellationToken = default);
    Task<AuditLogDetailDto> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}

public sealed class AuditLogService(IApplicationDbContext context) : IAuditLogService
{
    public async Task<PagedResult<AuditLogListItemDto>> GetPagedAsync(
        AuditLogFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = context.AuditLogs;

        if (!string.IsNullOrWhiteSpace(filter.EntityName))
        {
            if (!AuditableEntities.TryGetByName(filter.EntityName.Trim(), out var entity))
            {
                throw new ValidationException("La entidad debe ser Products, Categories, Sales, SaleDetails, StockMovements o Users.");
            }

            var name = entity.EntityName;
            query = query.Where(log => log.EntityName == name);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            var entityId = filter.EntityId.Trim();
            query = query.Where(log => log.EntityId == entityId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            if (!Enum.TryParse<AuditAction>(filter.Action, ignoreCase: true, out var action) || !Enum.IsDefined(action))
            {
                throw new ValidationException("La acción debe ser INSERT, UPDATE o DELETE.");
            }

            query = query.Where(log => log.Action == action);
        }

        if (filter.UserId.HasValue)
            query = query.Where(log => log.UserId == filter.UserId.Value);

        if (filter.From.HasValue)
            query = query.Where(log => log.OccurredAtUtc >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(log => log.OccurredAtUtc <= filter.To.Value);

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            var correlationId = filter.CorrelationId.Trim();
            query = query.Where(log => log.CorrelationId == correlationId);
        }

        var total = await query.CountAsync(cancellationToken);

        // La lista no lee OldValues/NewValues: solo columnas de resumen.
        var rows = await query
            .OrderByDescending(log => log.OccurredAtUtc)
            .ThenByDescending(log => log.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(log => new
            {
                log.Id, log.EntityName, log.EntityId, log.Action, log.UserId, log.ActorType, log.Source,
                log.ChangedFields, log.OccurredAtUtc, log.CorrelationId
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => new AuditLogListItemDto(
            row.Id,
            row.EntityName,
            row.EntityId,
            row.Action.ToString(),
            row.UserId,
            row.ActorType.ToString(),
            row.Source.ToString(),
            SplitFields(row.ChangedFields),
            Utc(row.OccurredAtUtc),
            row.CorrelationId)).ToList();

        return new PagedResult<AuditLogListItemDto>(items, total, filter.PageNumber, filter.PageSize);
    }

    public async Task<AuditLogDetailDto> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var log = await context.AuditLogs.FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(AuditLog), id);

        return new AuditLogDetailDto(
            log.Id,
            log.EntityName,
            log.EntityId,
            log.Action.ToString(),
            log.UserId,
            log.ActorType.ToString(),
            log.Source.ToString(),
            SplitFields(log.ChangedFields),
            Utc(log.OccurredAtUtc),
            log.CorrelationId,
            SafeValues(log.EntityName, log.OldValues),
            SafeValues(log.EntityName, log.NewValues));
    }

    /// <summary>
    /// Defensa en profundidad: aunque el DbContext ya escribe solo propiedades de la lista blanca, el detalle descarta
    /// cualquier clave que no figure en ella (p. ej. una fila anómala insertada fuera de la aplicación).
    /// </summary>
    private static JsonElement? SafeValues(string entityName, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !AuditableEntities.TryGetByName(entityName, out var entity))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var allowed = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (entity.IsAuditable(property.Name))
                    allowed[property.Name] = property.Value.Clone();
            }

            return JsonSerializer.SerializeToElement(allowed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SplitFields(string fields) =>
        fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
