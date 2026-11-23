using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements", "dbo", t =>
        {
            t.HasCheckConstraint("CK_StockMovements_Type", "[MovementType] IN ('IN', 'OUT', 'ADJUSTMENT')");
            t.HasCheckConstraint("CK_StockMovements_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_StockMovements_Stock", "[PreviousStock] >= 0 AND [NewStock] >= 0");
            t.HasCheckConstraint("CK_StockMovements_Balance",
                "([MovementType] = 'IN' AND [NewStock] = [PreviousStock] + [Quantity]) " +
                "OR ([MovementType] = 'OUT' AND [NewStock] = [PreviousStock] - [Quantity]) " +
                "OR ([MovementType] = 'ADJUSTMENT' AND ABS([NewStock] - [PreviousStock]) = [Quantity])");
            t.HasCheckConstraint("CK_StockMovements_SaleType", "[SaleId] IS NULL OR [MovementType] = 'OUT'");
        });

        builder.HasKey(sm => sm.Id);
        builder.Property(sm => sm.Id)
            .ValueGeneratedOnAdd();

        builder.Property(sm => sm.ProductId)
            .IsRequired();

        builder.Property(sm => sm.UserId)
            .IsRequired();

        builder.Property(sm => sm.SaleId)
            .IsRequired(false);

        builder.Property(sm => sm.MovementType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(sm => sm.Quantity)
            .IsRequired();

        builder.Property(sm => sm.PreviousStock)
            .IsRequired();

        builder.Property(sm => sm.NewStock)
            .IsRequired();

        builder.Property(sm => sm.Reason)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(sm => sm.CreatedAt)
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        // Relación con Product
        builder.HasOne(sm => sm.Product)
            .WithMany(p => p.StockMovements)
            .HasForeignKey(sm => sm.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockMovements_Products");

        // Relación con User
        builder.HasOne(sm => sm.User)
            .WithMany(u => u.StockMovements)
            .HasForeignKey(sm => sm.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockMovements_Users");

        // Relación con Sale
        builder.HasOne(sm => sm.Sale)
            .WithMany(s => s.StockMovements)
            .HasForeignKey(sm => sm.SaleId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockMovements_Sales");

        // Índices
        builder.HasIndex(sm => new { sm.ProductId, sm.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_StockMovements_ProductDate");

        builder.HasIndex(sm => sm.SaleId)
            .HasDatabaseName("IX_StockMovements_SaleId");
    }
}
