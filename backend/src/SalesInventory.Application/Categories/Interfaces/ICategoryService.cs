using SalesInventory.Application.Categories.DTOs;

namespace SalesInventory.Application.Categories.Interfaces;

public interface ICategoryService
{
    Task<IReadOnlyList<CategoryResponseDto>> GetAllAsync(bool? activeOnly = null, CancellationToken cancellationToken = default);
    Task<CategoryResponseDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<CategoryResponseDto> CreateAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default);
    Task<CategoryResponseDto> UpdateAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
