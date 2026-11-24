namespace SalesInventory.Domain.Entities;

/// <summary>Operaciones que admiten clave de idempotencia.</summary>
public static class IdempotencyOperations
{
    public const string CreateSale = "CreateSale";
}

/// <summary>
/// Registro de una operación idempotente ya confirmada. Se persiste en la misma transacción que la venta.
/// Solo guarda la clave, un hash SHA-256 del contenido y la referencia al resultado: nunca el cuerpo
/// de la solicitud, contraseñas ni tokens.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public Guid Key { get; set; }
    /// <summary>SHA-256 hexadecimal (64 caracteres) del contenido canónico de la solicitud.</summary>
    public string RequestHash { get; set; } = string.Empty;
    public int SaleId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public Sale? Sale { get; set; }
}
