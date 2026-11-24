using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Inventory.DTOs;

namespace SalesInventory.Application.Inventory.Interfaces;

public interface IInventoryService
{
    Task<PagedResult<StockMovementResponseDto>> GetMovementsAsync(MovementFilterParams filter, CancellationToken cancellationToken = default);
    Task<StockMovementResponseDto> RegisterEntryAsync(StockEntryRequestDto request, CancellationToken cancellationToken = default);
    Task<StockMovementResponseDto> RegisterAdjustmentAsync(StockAdjustmentRequestDto request, CancellationToken cancellationToken = default);
}
