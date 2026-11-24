using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public const string UniqueIndexName = "UX_ProcessedEvents_EventId_Consumer";

    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("ProcessedEvents", "dbo");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.EventId).IsRequired();

        builder.Property(e => e.Consumer)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(e => e.EventType)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(e => e.ProcessedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500);

        // Un mismo consumidor no puede registrar dos veces el mismo evento.
        builder.HasIndex(e => new { e.EventId, e.Consumer })
            .IsUnique()
            .HasDatabaseName(UniqueIndexName);
    }
}
