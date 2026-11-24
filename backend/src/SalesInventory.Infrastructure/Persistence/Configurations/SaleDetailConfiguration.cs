using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class SaleDetailConfiguration : IEntityTypeConfiguration<SaleDetail>
{
    public void Configure(EntityTypeBuilder<SaleDetail> builder)
    {
        builder.ToTable("SaleDetails", "dbo", t =>
        {
            t.HasCheckConstraint("CK_SaleDetails_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_SaleDetails_Price", "[UnitPrice] > 0");
            t.HasCheckConstraint("CK_SaleDetails_Subtotal", "[Subtotal] = [Quantity] * [UnitPrice]");
        });

        builder.HasKey(sd => sd.Id);
        builder.Property(sd => sd.Id)
            .ValueGeneratedOnAdd();

        builder.Property(sd => sd.SaleId)
            .IsRequired();

        builder.Property(sd => sd.ProductId)
            .IsRequired();

        builder.Property(sd => sd.Quantity)
            .IsRequired();

        builder.Property(sd => sd.UnitPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(sd => sd.Subtotal)
            .HasPrecision(18, 2)
            .IsRequired();

        // Restricción de unicidad para evitar productos duplicados en la misma venta
        builder.HasIndex(sd => new { sd.SaleId, sd.ProductId })
            .IsUnique()
            .HasDatabaseName("UQ_SaleDetails_SaleProduct");

        // Relación con Sale
        builder.HasOne(sd => sd.Sale)
            .WithMany(s => s.SaleDetails)
            .HasForeignKey(sd => sd.SaleId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SaleDetails_Sales");

        // Relación con Product
        builder.HasOne(sd => sd.Product)
            .WithMany(p => p.SaleDetails)
            .HasForeignKey(sd => sd.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_SaleDetails_Products");
    }
}
