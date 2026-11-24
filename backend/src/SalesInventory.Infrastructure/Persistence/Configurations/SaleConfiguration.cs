using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales", "dbo", t =>
        {
            t.HasCheckConstraint("CK_Sales_Total", "[Total] > 0");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .ValueGeneratedOnAdd();

        builder.Property(s => s.UserId)
            .IsRequired();

        builder.Property(s => s.SaleDate)
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.Property(s => s.Total)
            .HasPrecision(18, 2)
            .IsRequired();

        // Relación con User
        builder.HasOne(s => s.User)
            .WithMany(u => u.Sales)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Sales_Users");

        builder.HasIndex(s => s.SaleDate)
            .HasDatabaseName("IX_Sales_SaleDate");

        builder.HasIndex(s => s.UserId)
            .HasDatabaseName("IX_Sales_UserId");
    }
}
