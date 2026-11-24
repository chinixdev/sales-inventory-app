using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string PendingIndexName = "IX_OutboxMessages_Pending";
    public const string LeaseIndexName = "IX_OutboxMessages_ProcessingLease";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", "dbo", t =>
        {
            t.HasCheckConstraint("CK_OutboxMessages_Status",
                "[Status] IN ('Pending', 'Processing', 'Processed', 'Failed')");
            t.HasCheckConstraint("CK_OutboxMessages_RetryCount", "[RetryCount] >= 0");
            t.HasCheckConstraint("CK_OutboxMessages_Payload", "ISJSON([Payload]) = 1");
            t.HasCheckConstraint("CK_OutboxMessages_Processed",
                "([Status] = 'Processed' AND [ProcessedAtUtc] IS NOT NULL) OR ([Status] <> 'Processed' AND [ProcessedAtUtc] IS NULL)");
            t.HasCheckConstraint("CK_OutboxMessages_Lock",
                "([LockId] IS NULL AND [LockedUntilUtc] IS NULL) OR ([LockId] IS NOT NULL AND [LockedUntilUtc] IS NOT NULL)");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.EventType)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(m => m.Payload)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(m => m.OccurredAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(m => m.ProcessedAtUtc).HasColumnType("datetime2");
        builder.Property(m => m.FailedAtUtc).HasColumnType("datetime2");

        builder.Property(m => m.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(m => m.RetryCount).IsRequired();
        builder.Property(m => m.NextAttemptAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.Property(m => m.LockId);
        builder.Property(m => m.LockedUntilUtc).HasColumnType("datetime2");

        // Contexto W3C de trazabilidad (opcional: los eventos anteriores no lo tienen).
        builder.Property(m => m.TraceParent).HasMaxLength(55).IsUnicode(false);
        builder.Property(m => m.TraceState).HasMaxLength(512).IsUnicode(false);

        // Control de concurrencia optimista (mismo patrón que Product).
        builder.Property(m => m.RowVersion)
            .IsRowVersion()
            .IsRequired();

        // Cola de pendientes: el procesador lee los listos por NextAttemptAtUtc (y antigüedad) sin recorrer lo procesado.
        builder.HasIndex(m => new { m.NextAttemptAtUtc, m.OccurredAtUtc })
            .HasDatabaseName(PendingIndexName)
            .HasFilter($"[Status] = '{nameof(OutboxMessageStatus.Pending)}'");

        // Series y conteos del centro de monitoreo (últimas 24 h) sin recorrer la tabla completa.
        builder.HasIndex(m => m.ProcessedAtUtc)
            .HasDatabaseName("IX_OutboxMessages_ProcessedAt")
            .HasFilter($"[Status] = '{nameof(OutboxMessageStatus.Processed)}'");

        builder.HasIndex(m => m.FailedAtUtc)
            .HasDatabaseName("IX_OutboxMessages_FailedAt")
            .HasFilter($"[Status] = '{nameof(OutboxMessageStatus.Failed)}'");

        // Recuperación de arrendamientos vencidos de mensajes en procesamiento.
        builder.HasIndex(m => m.LockedUntilUtc)
            .HasDatabaseName(LeaseIndexName)
            .HasFilter($"[Status] = '{nameof(OutboxMessageStatus.Processing)}'");
    }
}
