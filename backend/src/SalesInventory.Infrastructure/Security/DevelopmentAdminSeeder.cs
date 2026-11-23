using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Users;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Infrastructure.Security;

public sealed class DevelopmentAdminSeeder(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    IPasswordHasher passwordHasher,
    ILogger<DevelopmentAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var configuredEmail = configuration["DevelopmentAdmin:Email"];
        var password = configuration["DevelopmentAdmin:Password"];

        if (string.IsNullOrWhiteSpace(configuredEmail) && string.IsNullOrEmpty(password))
        {
            logger.LogWarning(
                "Development administrator was not seeded; configure DevelopmentAdmin credentials using user secrets or environment variables.");
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredEmail) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Configure both DevelopmentAdmin:Email and DevelopmentAdmin:Password.");
        }

        var email = UserAccountValidation.NormalizeEmail(configuredEmail);
        UserAccountValidation.ValidatePassword(password);
        var fullName = configuration["DevelopmentAdmin:FullName"] ?? "Development Administrator";
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 120)
        {
            throw new InvalidOperationException(
                "DevelopmentAdmin:FullName must contain between 1 and 120 characters.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SalesInventoryDbContext>();

        if (await context.Users.AnyAsync(user => user.Role == UserRole.Admin, cancellationToken))
        {
            logger.LogInformation("A development administrator already exists; no user was created.");
            return;
        }

        if (await context.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            throw new InvalidOperationException(
                "DevelopmentAdmin:Email is already assigned to a non-administrator account.");
        }

        context.Users.Add(new User
        {
            FullName = fullName,
            Email = email,
            PasswordHash = passwordHasher.HashPassword(password),
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Development administrator was created.");
    }
}
