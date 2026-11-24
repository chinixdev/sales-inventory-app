using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Sales.DTOs;

namespace SalesInventory.Application.Sales.Interfaces;

public interface ISaleService
{
    Task<SaleResponseDto> CreateAsync(CreateSaleRequestDto request, CancellationToken cancellationToken = default);
    Task<SaleResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<PagedResult<SaleResponseDto>> GetPagedAsync(SaleFilterParams filter, CancellationToken cancellationToken = default);
}
