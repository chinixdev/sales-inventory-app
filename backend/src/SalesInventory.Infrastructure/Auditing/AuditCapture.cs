using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SalesInventory.Application.Audit;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Auditing;

/// <summary>
/// Instantánea de los cambios auditables pendientes de un SaveChanges. Se toma ANTES de guardar (estados y valores
/// anteriores) y las filas de AuditLog se construyen DESPUÉS de guardar, cuando SQL Server ya asignó los
/// identificadores definitivos de los INSERT. Solo se leen propiedades de la lista blanca (<see cref="AuditableEntities"/>).
/// </summary>
internal sealed class AuditCapture
{
    private sealed class Candidate(AuditableEntity definition, EntityEntry entry, AuditAction action)
    {
        public AuditableEntity Definition { get; } = definition;
        public EntityEntry Entry { get; } = entry;
        public AuditAction Action { get; } = action;
        public Dictionary<string, object?>? OldValues { get; set; }
        public Dictionary<string, object?>? NewValues { get; set; }
        public List<string> ChangedFields { get; } = [];
        public string? EntityId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly List<Candidate> _candidates = [];

    public bool IsEmpty => _candidates.Count == 0;

    public static AuditCapture From(ChangeTracker tracker)
    {
        var capture = new AuditCapture();

        // Se materializa la lista: leer el estado no debe interferir con el rastreador.
        foreach (var entry in tracker.Entries().ToList())
        {
            if (!AuditableEntities.TryGet(entry.Entity.GetType(), out var definition))
                continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    // Los valores y el Id definitivo se leen después de guardar.
                    capture._candidates.Add(new Candidate(definition, entry, AuditAction.INSERT));
                    break;

                case EntityState.Modified:
                    var update = new Candidate(definition, entry, AuditAction.UPDATE)
                    {
                        OldValues = new Dictionary<string, object?>(),
                        NewValues = new Dictionary<string, object?>(),
                        EntityId = KeyOf(entry, definition, original: false)
                    };

                    foreach (var name in definition.Properties)
                    {
                        var property = entry.Property(name);
                        if (!property.IsModified || Equals(property.OriginalValue, property.CurrentValue))
                            continue;

                        update.OldValues[name] = Normalize(property.OriginalValue);
                        update.NewValues[name] = Normalize(property.CurrentValue);
                        update.ChangedFields.Add(name);
                    }

                    // Operación sin cambios en propiedades auditables: no genera registro.
                    if (update.ChangedFields.Count > 0)
                        capture._candidates.Add(update);
                    break;

                case EntityState.Deleted:
                    var delete = new Candidate(definition, entry, AuditAction.DELETE)
                    {
                        OldValues = Snapshot(entry, definition, original: true),
                        EntityId = KeyOf(entry, definition, original: true)
                    };
                    delete.ChangedFields.AddRange(definition.Properties);
                    capture._candidates.Add(delete);
                    break;
            }
        }

        return capture;
    }

    /// <summary>
    /// Tras un intento fallido de la estrategia de reintentos, las claves ya asignadas por una transacción revertida
    /// se vuelven temporales para que el siguiente intento no intente insertarlas con valores explícitos.
    /// </summary>
    public void ResetGeneratedKeys()
    {
        foreach (var candidate in _candidates.Where(c => c.Action == AuditAction.INSERT))
        {
            candidate.Entry.Property(candidate.Definition.KeyProperty).IsTemporary = true;
        }
    }

    public List<AuditLog> Build(AuditActor actor, DateTime occurredAtUtc)
    {
        var logs = new List<AuditLog>(_candidates.Count);
        foreach (var candidate in _candidates)
        {
            Dictionary<string, object?>? oldValues = candidate.OldValues;
            Dictionary<string, object?>? newValues = candidate.NewValues;
            var entityId = candidate.EntityId;
            var changed = candidate.ChangedFields;

            if (candidate.Action == AuditAction.INSERT)
            {
                // Después de guardar: el Id es el definitivo de SQL Server y las FK ya están propagadas.
                entityId = KeyOf(candidate.Entry, candidate.Definition, original: false);
                newValues = Snapshot(candidate.Entry, candidate.Definition, original: false);
                changed = candidate.Definition.Properties.ToList();
            }

            logs.Add(new AuditLog
            {
                EntityName = candidate.Definition.EntityName,
                EntityId = entityId ?? string.Empty,
                Action = candidate.Action,
                UserId = actor.ActorType == AuditActorType.User ? actor.UserId : null,
                ActorType = actor.ActorType,
                Source = actor.Source,
                OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues, JsonOptions),
                NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues, JsonOptions),
                ChangedFields = string.Join(',', changed),
                OccurredAtUtc = occurredAtUtc,
                CorrelationId = Truncate(actor.CorrelationId, 64)
            });
        }

        return logs;
    }

    private static Dictionary<string, object?> Snapshot(EntityEntry entry, AuditableEntity definition, bool original)
    {
        var values = new Dictionary<string, object?>();
        foreach (var name in definition.Properties)
        {
            var property = entry.Property(name);
            values[name] = Normalize(original ? property.OriginalValue : property.CurrentValue);
        }

        return values;
    }

    private static string? KeyOf(EntityEntry entry, AuditableEntity definition, bool original)
    {
        var property = entry.Property(definition.KeyProperty);
        return Convert.ToString(original ? property.OriginalValue : property.CurrentValue, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? Normalize(object? value) => value switch
    {
        Enum e => e.ToString(),
        _ => value
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
