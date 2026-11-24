namespace SalesInventory.Domain.Exceptions;

public class InsufficientStockException : DomainException
{
    public int ProductId { get; }
    public int RequestedQuantity { get; }
    public int AvailableStock { get; }

    public InsufficientStockException(int productId, int requestedQuantity, int availableStock)
        : base($"Stock insuficiente para el producto {productId}. Solicitado: {requestedQuantity}, Disponible: {availableStock}")
    {
        ProductId = productId;
        RequestedQuantity = requestedQuantity;
        AvailableStock = availableStock;
    }
}
