using SalesInventory.Domain.Entities;

namespace SalesInventory.Application.Audit;

/// <summary>Definición explícita de una entidad auditable: solo estas propiedades pueden llegar a un AuditLog.</summary>
public sealed record AuditableEntity(Type ClrType, string EntityName, string KeyProperty, IReadOnlyList<string> Properties)
{
    private readonly HashSet<string> _lookup = new(Properties, StringComparer.OrdinalIgnoreCase);

    public bool IsAuditable(string property) => _lookup.Contains(property);
}

/// <summary>
/// LISTA BLANCA de entidades y propiedades auditables. Todo lo que no figure aquí se ignora: contraseñas, hashes,
/// tokens, RowVersion, claves de idempotencia, outbox, notificaciones, etc. nunca se auditan. Agregar una propiedad
/// es una decisión consciente y revisable en esta lista (no se descubre por reflexión).
/// </summary>
public static class AuditableEntities
{
    private static readonly AuditableEntity[] Definitions =
    [
        new(typeof(Product), "Products", nameof(Product.Id),
            [nameof(Product.CategoryId), nameof(Product.SKU), nameof(Product.Name), nameof(Product.Description),
             nameof(Product.Price), nameof(Product.Stock), nameof(Product.MinimumStock), nameof(Product.IsActive)]),

        new(typeof(Category), "Categories", nameof(Category.Id),
            [nameof(Category.Name), nameof(Category.IsActive)]),

        new(typeof(Sale), "Sales", nameof(Sale.Id),
            [nameof(Sale.UserId), nameof(Sale.SaleDate), nameof(Sale.Total)]),

        new(typeof(SaleDetail), "SaleDetails", nameof(SaleDetail.Id),
            [nameof(SaleDetail.SaleId), nameof(SaleDetail.ProductId), nameof(SaleDetail.Quantity),
             nameof(SaleDetail.UnitPrice), nameof(SaleDetail.Subtotal)]),

        new(typeof(StockMovement), "StockMovements", nameof(StockMovement.Id),
            [nameof(StockMovement.ProductId), nameof(StockMovement.UserId), nameof(StockMovement.SaleId),
             nameof(StockMovement.MovementType), nameof(StockMovement.Quantity), nameof(StockMovement.PreviousStock),
             nameof(StockMovement.NewStock), nameof(StockMovement.Reason), nameof(StockMovement.CreatedAt)]),

        // Solo cambios administrativos: PasswordHash y cualquier credencial quedan fuera a propósito.
        new(typeof(User), "Users", nameof(User.Id),
            [nameof(User.FullName), nameof(User.Email), nameof(User.Role), nameof(User.IsActive)])
    ];

    private static readonly Dictionary<Type, AuditableEntity> ByTypeIndex = Definitions.ToDictionary(d => d.ClrType);
    private static readonly Dictionary<string, AuditableEntity> ByNameIndex =
        Definitions.ToDictionary(d => d.EntityName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<AuditableEntity> All => Definitions;

    public static bool TryGet(Type clrType, out AuditableEntity definition) => ByTypeIndex.TryGetValue(clrType, out definition!);

    public static bool TryGetByName(string entityName, out AuditableEntity definition) =>
        ByNameIndex.TryGetValue(entityName, out definition!);
}
