using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public const string UniqueIndexName = "UX_IdempotencyRecords_User_Operation_Key";

    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords", "dbo");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.UserId).IsRequired();

        builder.Property(r => r.Operation)
            .HasMaxLength(50)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(r => r.Key)
            .HasColumnName("IdempotencyKey")
            .IsRequired();

        builder.Property(r => r.RequestHash)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsUnicode(false)
            .IsRequired();

        builder.Property(r => r.SaleId).IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IdempotencyRecords_Users");

        builder.HasOne(r => r.Sale)
            .WithMany()
            .HasForeignKey(r => r.SaleId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IdempotencyRecords_Sales");

        // Garantiza una sola operación por (usuario, operación, clave), incluso con solicitudes concurrentes.
        builder.HasIndex(r => new { r.UserId, r.Operation, r.Key })
            .IsUnique()
            .HasDatabaseName(UniqueIndexName);

        builder.HasIndex(r => r.SaleId)
            .HasDatabaseName("IX_IdempotencyRecords_SaleId");
    }
}
