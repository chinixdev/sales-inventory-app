using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Auth.DTOs;
using SalesInventory.Application.Auth.Interfaces;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Users;

namespace SalesInventory.Application.Auth.Services;

public sealed class AuthService(
    IApplicationDbContext context,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator tokenGenerator) : IAuthService
{
    public async Task<LoginResponseDto?> LoginAsync(
        LoginRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!UserAccountValidation.IsValidEmail(request.Email))
        {
            return null;
        }

        var email = UserAccountValidation.NormalizeEmail(request.Email);
        var user = await context.Users
            .FirstOrDefaultAsync(
                candidate => candidate.Email.ToLower() == email,
                cancellationToken);

        if (user is null || !passwordHasher.VerifyPassword(user, request.Password))
        {
            return null;
        }

        return tokenGenerator.Generate(user);
    }
}
