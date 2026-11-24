using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Common.Models;
using SalesInventory.Domain.Entities;
using ValidationException = SalesInventory.Application.Common.Exceptions.ValidationException;

namespace SalesInventory.Application.Notifications;

public sealed record NotificationResponseDto(
    long Id,
    string Type,
    int ProductId,
    string Title,
    string Message,
    int CurrentStock,
    int MinimumStock,
    DateTime CreatedAtUtc,
    bool IsRead,
    DateTime? ReadAtUtc);

public sealed record UnreadCountDto(int Count);

public sealed record MarkedReadDto(int Updated);

public class NotificationFilterParams
{
    [Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    /// <summary>Si es true solo devuelve las no leídas; si es false solo las leídas.</summary>
    public bool? Unread { get; set; }

    /// <summary>LowStock u OutOfStock.</summary>
    public string? Type { get; set; }
}

public interface INotificationService
{
    Task<PagedResult<NotificationResponseDto>> GetPagedAsync(int userId, NotificationFilterParams filter, CancellationToken cancellationToken = default);
    Task<int> CountUnreadAsync(int userId, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(int userId, long notificationId, CancellationToken cancellationToken = default);
    Task<int> MarkAllAsReadAsync(int userId, CancellationToken cancellationToken = default);
}

/// <summary>Notificaciones propias del usuario autenticado: toda consulta y escritura se acota por UserId.</summary>
public sealed class NotificationService(IApplicationDbContext context, TimeProvider clock) : INotificationService
{
    public async Task<PagedResult<NotificationResponseDto>> GetPagedAsync(
        int userId,
        NotificationFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = context.Notifications.AsNoTracking().Where(n => n.UserId == userId);

        if (filter.Unread == true)
            query = query.Where(n => n.ReadAtUtc == null);
        else if (filter.Unread == false)
            query = query.Where(n => n.ReadAtUtc != null);

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            if (!Enum.TryParse<NotificationType>(filter.Type, ignoreCase: true, out var type)
                || !Enum.IsDefined(type))
            {
                throw new ValidationException("El tipo de notificación debe ser LowStock u OutOfStock.");
            }

            query = query.Where(n => n.Type == type);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(n => new NotificationResponseDto(
            n.Id,
            n.Type.ToString(),
            n.ProductId,
            n.Title,
            n.Message,
            n.CurrentStock,
            n.MinimumStock,
            DateTime.SpecifyKind(n.CreatedAtUtc, DateTimeKind.Utc),
            n.ReadAtUtc != null,
            n.ReadAtUtc is null ? null : DateTime.SpecifyKind(n.ReadAtUtc.Value, DateTimeKind.Utc))).ToList();

        return new PagedResult<NotificationResponseDto>(items, total, filter.PageNumber, filter.PageSize);
    }

    public Task<int> CountUnreadAsync(int userId, CancellationToken cancellationToken = default) =>
        context.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);

    public async Task MarkAsReadAsync(int userId, long notificationId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await context.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAtUtc, (DateTime?)now), cancellationToken);

        if (updated > 0)
            return;

        // Marcar dos veces es idempotente; una notificación ajena o inexistente es 404 (sin revelar su existencia).
        var ownedAndAlreadyRead = await context.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.Id == notificationId && n.UserId == userId, cancellationToken);
        if (!ownedAndAlreadyRead)
            throw new NotFoundException(nameof(Notification), notificationId);
    }

    public Task<int> MarkAllAsReadAsync(int userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return context.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAtUtc, (DateTime?)now), cancellationToken);
    }
}
