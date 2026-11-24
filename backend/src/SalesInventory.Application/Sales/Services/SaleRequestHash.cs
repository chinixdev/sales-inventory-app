using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SalesInventory.Application.Sales.DTOs;

namespace SalesInventory.Application.Sales.Services;

/// <summary>
/// Hash SHA-256 del contenido lógico de una solicitud de venta: pares (producto, cantidad) en orden
/// determinista, de modo que el orden de los ítems no altera el hash pero cualquier diferencia de
/// producto, cantidad o número de líneas (incluidas líneas repetidas) sí lo hace.
/// No incluye usuario (ya forma parte de la clave única) ni datos sensibles.
/// </summary>
public static class SaleRequestHash
{
    public static string Compute(IEnumerable<SaleItemRequestDto> items)
    {
        var canonical = string.Join(
            ';',
            items
                .OrderBy(item => item.ProductId)
                .ThenBy(item => item.Quantity)
                .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ProductId}:{item.Quantity}")));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
