using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public const string TimelineIndexName = "IX_AuditLogs_OccurredAt";
    public const string EntityIndexName = "IX_AuditLogs_Entity";
    public const string UserIndexName = "IX_AuditLogs_User";
    public const string CorrelationIndexName = "IX_AuditLogs_Correlation";

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", "dbo", t =>
        {
            t.HasCheckConstraint("CK_AuditLogs_Action", "[Action] IN ('INSERT', 'UPDATE', 'DELETE')");
            t.HasCheckConstraint("CK_AuditLogs_ActorType", "[ActorType] IN ('User', 'System')");
            t.HasCheckConstraint("CK_AuditLogs_Source", "[Source] IN ('API', 'Worker')");
            t.HasCheckConstraint("CK_AuditLogs_Actor", "([ActorType] = 'User' AND [UserId] IS NOT NULL) OR ([ActorType] = 'System' AND [UserId] IS NULL)");
            t.HasCheckConstraint("CK_AuditLogs_Json",
                "([OldValues] IS NULL OR ISJSON([OldValues]) = 1) AND ([NewValues] IS NULL OR ISJSON([NewValues]) = 1)");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.EntityName).HasMaxLength(50).IsUnicode(false).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(64).IsUnicode(false).IsRequired();

        builder.Property(a => a.Action).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(a => a.Source).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();

        // Sin FK a Users a propósito: el historial no debe impedir ni verse afectado por cambios en Users.
        builder.Property(a => a.UserId);

        builder.Property(a => a.OldValues).HasColumnType("nvarchar(max)");
        builder.Property(a => a.NewValues).HasColumnType("nvarchar(max)");
        builder.Property(a => a.ChangedFields).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.OccurredAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(64).IsUnicode(false).IsRequired();

        // Listado paginado (más recientes primero) y filtros por rango de fechas.
        builder.HasIndex(a => new { a.OccurredAtUtc, a.Id })
            .IsDescending(true, true)
            .HasDatabaseName(TimelineIndexName);

        // Historial de una entidad concreta.
        builder.HasIndex(a => new { a.EntityName, a.EntityId, a.OccurredAtUtc })
            .HasDatabaseName(EntityIndexName);

        // Actividad de un usuario.
        builder.HasIndex(a => new { a.UserId, a.OccurredAtUtc })
            .HasDatabaseName(UserIndexName);

        // Todos los cambios de una misma solicitud / unidad de trabajo.
        builder.HasIndex(a => a.CorrelationId)
            .HasDatabaseName(CorrelationIndexName);
    }
}
