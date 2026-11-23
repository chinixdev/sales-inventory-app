using SalesInventory.Application.Auth.DTOs;

namespace SalesInventory.Application.Auth.Interfaces;

public interface IAuthService
{
    Task<LoginResponseDto?> LoginAsync(
        LoginRequestDto request,
        CancellationToken cancellationToken = default);
}
