using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence.Configurations;

public class WorkerHeartbeatConfiguration : IEntityTypeConfiguration<WorkerHeartbeat>
{
    public void Configure(EntityTypeBuilder<WorkerHeartbeat> builder)
    {
        builder.ToTable("WorkerHeartbeats", "dbo");
        builder.HasKey(h => h.InstanceId);
        builder.Property(h => h.InstanceId).ValueGeneratedNever();
        builder.Property(h => h.StartedAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(h => h.LastSeenAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(h => h.LastSuccessfulCycleAtUtc).HasColumnType("datetime2");
        builder.Property(h => h.LastCycleErrorType).HasMaxLength(100).IsUnicode(false);
        builder.Property(h => h.Version).HasMaxLength(50).IsUnicode(false).IsRequired();
    }
}
