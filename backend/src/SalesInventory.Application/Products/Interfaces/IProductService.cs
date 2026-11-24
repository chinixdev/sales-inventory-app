using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Products.DTOs;

namespace SalesInventory.Application.Products.Interfaces;

public interface IProductService
{
    Task<PagedResult<ProductResponseDto>> GetPagedAsync(ProductFilterParams filter, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> CreateAsync(CreateProductRequestDto request, CancellationToken cancellationToken = default);
    Task<ProductResponseDto> UpdateAsync(int id, UpdateProductRequestDto request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
