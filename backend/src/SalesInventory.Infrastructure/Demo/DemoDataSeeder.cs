using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Users;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Infrastructure.Demo;

/// <summary>Sección <c>Demo</c>. Las contraseñas no tienen valor por defecto: se definen por variables de entorno del host.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }
    public string SellerEmail { get; set; } = "demo.seller@salesinventory.demo";
    public string? SellerPassword { get; set; }

    /// <summary>Opcional: sin contraseña no se crea el administrador de demostración (la demo pública queda sin permisos de escritura).</summary>
    public string AdminEmail { get; set; } = "demo.admin@salesinventory.demo";
    public string? AdminPassword { get; set; }
}

/// <summary>
/// Carga datos de demostración (usuarios, catálogo, stock y ventas recientes) para que una instalación nueva se vea completa.
/// Es idempotente: si ya existen productos no hace nada, así que nunca pisa datos reales.
/// </summary>
public sealed class DemoDataSeeder(
    SalesInventoryDbContext db,
    IPasswordHasher passwordHasher,
    IOptions<DemoOptions> options,
    ILogger<DemoDataSeeder> logger)
{
    private readonly DemoOptions _options = options.Value;

    private sealed record ProductSeed(string Category, string Sku, string Name, decimal Price, int InitialStock, int MinimumStock);

    private static readonly ProductSeed[] Catalog =
    [
        new("Computación", "DEMO-TEC-001", "Teclado mecánico", 189.90m, 60, 10),
        new("Computación", "DEMO-MOU-002", "Mouse inalámbrico", 79.90m, 80, 15),
        new("Computación", "DEMO-MON-003", "Monitor 24 pulgadas", 649.00m, 25, 5),
        new("Computación", "DEMO-LAP-004", "Laptop 14 pulgadas", 2899.00m, 12, 3),
        new("Accesorios", "DEMO-CAB-005", "Cable HDMI 2 m", 24.90m, 120, 20),
        new("Accesorios", "DEMO-USB-006", "Memoria USB 64 GB", 39.90m, 100, 20),
        new("Accesorios", "DEMO-AUD-007", "Audífonos con micrófono", 129.00m, 45, 10),
        new("Accesorios", "DEMO-WEB-008", "Cámara web HD", 159.00m, 30, 8),
        new("Oficina", "DEMO-SIL-009", "Silla ergonómica", 549.00m, 14, 4),
        new("Oficina", "DEMO-LAM-010", "Lámpara de escritorio", 69.90m, 35, 8),
        new("Oficina", "DEMO-PAP-011", "Resma de papel A4", 18.50m, 200, 40),
        // Pocas unidades a propósito: tras las ventas quedan en o bajo el mínimo y generan alertas de stock.
        new("Oficina", "DEMO-IMP-012", "Impresora multifunción", 899.00m, 9, 6),
        new("Accesorios", "DEMO-BAT-013", "Batería externa 10000 mAh", 89.90m, 14, 10)
    ];

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        ValidatePasswords();

        // La demo se despliega sobre una base vacía: aquí se aplican las migraciones (la API normal no migra sola).
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (await db.Products.AnyAsync(cancellationToken))
        {
            logger.LogInformation("La base ya tiene productos: no se cargan datos de demostración.");
            return;
        }

        var seller = await GetOrCreateUserAsync("Vendedor Demo", _options.SellerEmail, _options.SellerPassword!, UserRole.Seller, cancellationToken);
        var admin = string.IsNullOrEmpty(_options.AdminPassword)
            ? null
            : await GetOrCreateUserAsync("Administrador Demo", _options.AdminEmail, _options.AdminPassword, UserRole.Admin, cancellationToken);
        var stockOwner = admin ?? seller;

        var categories = Catalog.Select(p => p.Category).Distinct()
            .ToDictionary(name => name, name => new Category { Name = name });
        var products = Catalog.ToDictionary(
            seed => seed.Sku,
            seed => new Product
            {
                Category = categories[seed.Category],
                SKU = seed.Sku,
                Name = seed.Name,
                Price = seed.Price,
                Stock = seed.InitialStock,
                MinimumStock = seed.MinimumStock,
                CreatedAt = DateTime.UtcNow.AddDays(-20)
            });

        db.Categories.AddRange(categories.Values);
        db.Products.AddRange(products.Values);

        foreach (var seed in Catalog)
        {
            db.StockMovements.Add(new StockMovement
            {
                Product = products[seed.Sku],
                User = stockOwner,
                MovementType = StockMovementType.IN,
                Quantity = seed.InitialStock,
                PreviousStock = 0,
                NewStock = seed.InitialStock,
                Reason = "Stock inicial por alta de producto",
                CreatedAt = DateTime.UtcNow.AddDays(-20)
            });
        }

        AddRecentSales(products.Values.ToList(), seller);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Datos de demostración cargados: {Products} productos.", products.Count);
    }

    /// <summary>Ventas de los últimos 14 días, deterministas (misma semilla), que respetan el stock y el kardex.</summary>
    private void AddRecentSales(IReadOnlyList<Product> products, User seller)
    {
        var random = new Random(42);
        for (var i = 0; i < 30; i++)
        {
            var date = DateTime.UtcNow.AddDays(-14 + i * 14.0 / 30).AddMinutes(random.Next(0, 600));
            var sale = new Sale { User = seller, SaleDate = date };

            foreach (var product in products.OrderBy(_ => random.Next()).Take(random.Next(1, 4)))
            {
                var quantity = random.Next(1, 4);
                if (product.Stock < quantity)
                {
                    continue;
                }

                var previousStock = product.Stock;
                product.DeductStock(quantity);

                var detail = new SaleDetail { Product = product, Quantity = quantity, UnitPrice = product.Price };
                detail.CalculateSubtotal();
                sale.SaleDetails.Add(detail);
                sale.StockMovements.Add(new StockMovement
                {
                    Product = product,
                    User = seller,
                    MovementType = StockMovementType.OUT,
                    Quantity = quantity,
                    PreviousStock = previousStock,
                    NewStock = product.Stock,
                    Reason = "Venta registrada",
                    CreatedAt = date
                });
            }

            if (sale.SaleDetails.Count == 0)
            {
                continue;
            }

            sale.RecalculateTotal();
            db.Sales.Add(sale);
        }
    }

    private async Task<User> GetOrCreateUserAsync(string fullName, string email, string password, UserRole role, CancellationToken cancellationToken)
    {
        var normalized = UserAccountValidation.NormalizeEmail(email);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var user = new User
        {
            FullName = fullName,
            Email = normalized,
            PasswordHash = passwordHasher.HashPassword(password),
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        return user;
    }

    private void ValidatePasswords()
    {
        if (!UserAccountValidation.IsValidPassword(_options.SellerPassword))
        {
            throw new InvalidOperationException(
                $"Demo:SellerPassword es obligatoria y debe tener entre {UserAccountValidation.MinimumPasswordLength} y {UserAccountValidation.MaximumPasswordLength} caracteres.");
        }

        if (!string.IsNullOrEmpty(_options.AdminPassword) && !UserAccountValidation.IsValidPassword(_options.AdminPassword))
        {
            throw new InvalidOperationException("Demo:AdminPassword, si se define, debe tener una longitud válida.");
        }
    }
}
