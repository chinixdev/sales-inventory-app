using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SalesInventory.Application.Auth.DTOs;
using SalesInventory.Domain.Enums;
using Xunit;

namespace SalesInventory.IntegrationTests;

[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class AuthEndpointTests(SqlServerConcurrencyDatabaseFixture fixture)
{
    [Fact]
    public async Task Login_WithValidCredentials_ReturnsSignedJwtWithExpectedClaims()
    {
        const string password = "Valid-Passphrase-2030";
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        var user = await fixture.SeedUserAsync(
            $"login-{Guid.NewGuid():N}@example.com",
            password);

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequestDto { Email = user.Email, Password = password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        login.Should().NotBeNull();
        login!.TokenType.Should().Be("Bearer");
        login.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        login.ExpiresAt.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(31));

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            login.AccessToken,
            JwtTestConfiguration.CreateValidationParameters(),
            out var validatedToken);
        validatedToken.Should().BeOfType<JwtSecurityToken>();
        ((JwtSecurityToken)validatedToken).Header.Alg.Should().Be(
            SecurityAlgorithms.HmacSha256);
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(user.Id.ToString());
        principal.FindFirst(ClaimTypes.Email)!.Value.Should().Be(user.Email);
        principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be(UserRole.Seller.ToString());
    }

    [Fact]
    public async Task Login_WithIncorrectPassword_ReturnsGenericUnauthorizedResponse()
    {
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        var user = await fixture.SeedUserAsync(
            $"wrong-password-{Guid.NewGuid():N}@example.com",
            "Valid-Passphrase-2030");

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequestDto { Email = user.Email, Password = "Invalid-Passphrase-2030" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("Email o contraseña incorrectos.");
    }

    [Fact]
    public async Task Login_WithInactiveUser_ReturnsSameGenericUnauthorizedResponse()
    {
        fixture.Client.DefaultRequestHeaders.Authorization = null;
        var user = await fixture.SeedUserAsync(
            $"inactive-{Guid.NewGuid():N}@example.com",
            "Valid-Passphrase-2030",
            isActive: false);

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequestDto { Email = user.Email, Password = "Valid-Passphrase-2030" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("Email o contraseña incorrectos.");
    }
}
