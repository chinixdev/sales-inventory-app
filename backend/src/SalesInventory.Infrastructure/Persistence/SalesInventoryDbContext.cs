using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Persistence;

public class SalesInventoryDbContext : DbContext, IApplicationDbContext
{
    public SalesInventoryDbContext(DbContextOptions<SalesInventoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleDetail> SaleDetails => Set<SaleDetail>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
