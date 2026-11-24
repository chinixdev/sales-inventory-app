using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public const string InboxIndexName = "IX_Notifications_User_Read_Created";
    public const string UniqueIndexName = "UX_Notifications_User_Event_Product_Type";

    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", "dbo", t =>
        {
            t.HasCheckConstraint("CK_Notifications_Type", "[Type] IN ('LowStock', 'OutOfStock')");
            t.HasCheckConstraint("CK_Notifications_Stock", "[CurrentStock] >= 0 AND [MinimumStock] >= 0");
        });

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedOnAdd();

        builder.Property(n => n.UserId).IsRequired();
        builder.Property(n => n.ProductId).IsRequired();

        builder.Property(n => n.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(n => n.Title).HasMaxLength(150).IsRequired();
        builder.Property(n => n.Message).HasMaxLength(500).IsRequired();
        builder.Property(n => n.CurrentStock).IsRequired();
        builder.Property(n => n.MinimumStock).IsRequired();
        builder.Property(n => n.SourceEventId).IsRequired();
        builder.Property(n => n.CreatedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(n => n.ReadAtUtc).HasColumnType("datetime2");

        builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Notifications_Users");

        builder.HasOne(n => n.Product)
            .WithMany()
            .HasForeignKey(n => n.ProductId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Notifications_Products");

        // Bandeja del usuario: no leídas primero y por fecha, sin recorrer las de otros usuarios.
        builder.HasIndex(n => new { n.UserId, n.ReadAtUtc, n.CreatedAtUtc })
            .HasDatabaseName(InboxIndexName);

        // Una misma entrega repetida no puede duplicar la alerta de un producto para un usuario.
        builder.HasIndex(n => new { n.UserId, n.SourceEventId, n.ProductId, n.Type })
            .IsUnique()
            .HasDatabaseName(UniqueIndexName);
    }
}
