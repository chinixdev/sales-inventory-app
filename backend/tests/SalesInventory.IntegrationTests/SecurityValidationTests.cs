using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SalesInventory.Application.Auth.DTOs;
using SalesInventory.Domain.Enums;

namespace SalesInventory.IntegrationTests;

[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SecurityValidationTests(SqlServerConcurrencyDatabaseFixture fixture)
{
    private const string ValidPassword = "Valid-Passphrase-2030";

    public static TheoryData<string, string> ProtectedEndpoints => new()
    {
        { "GET", "/api/categories" },
        { "GET", "/api/categories/1" },
        { "POST", "/api/categories" },
        { "PUT", "/api/categories/1" },
        { "DELETE", "/api/categories/1" },
        { "GET", "/api/products" },
        { "GET", "/api/products/1" },
        { "POST", "/api/products" },
        { "PUT", "/api/products/1" },
        { "DELETE", "/api/products/1" },
        { "GET", "/api/sales" },
        { "GET", "/api/sales/1" },
        { "POST", "/api/sales" },
        { "GET", "/api/inventory/movements" },
        { "POST", "/api/inventory/entries" },
        { "POST", "/api/inventory/adjustments" }
    };

    public static TheoryData<string, string> AdminOnlyEndpoints => new()
    {
        { "POST", "/api/categories" },
        { "PUT", "/api/categories/1" },
        { "DELETE", "/api/categories/1" },
        { "POST", "/api/products" },
        { "PUT", "/api/products/1" },
        { "DELETE", "/api/products/1" },
        { "POST", "/api/inventory/entries" },
        { "POST", "/api/inventory/adjustments" }
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task ProtectedEndpoint_WithoutJwt_ReturnsUnauthorized(string method, string path)
    {
        using var response = await SendAsync(method, path, authorization: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task ProtectedEndpoint_WithMalformedJwt_ReturnsUnauthorized(string method, string path)
    {
        using var response = await SendAsync(
            method,
            path,
            new AuthenticationHeaderValue("Bearer", "not.a.jwt"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyEndpoints))]
    public async Task AdminOnlyEndpoint_AsSeller_ReturnsForbidden(string method, string path)
    {
        var user = await fixture.SeedUserAsync($"seller-{Guid.NewGuid():N}@example.com", ValidPassword);
        var header = JwtTestConfiguration.CreateAuthorizationHeader(user.Id, nameof(UserRole.Seller));

        using var response = await SendAsync(method, path, header);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Jwt_WithWrongSignature_ReturnsUnauthorized()
    {
        var token = BuildToken(
            JwtTestConfiguration.Issuer,
            JwtTestConfiguration.Audience,
            "a-completely-different-signing-key-0123456789",
            DateTime.UtcNow.AddMinutes(10));

        await AssertUnauthorizedAsync(token);
    }

    [Fact]
    public async Task Jwt_Expired_ReturnsUnauthorized()
    {
        var now = DateTime.UtcNow;
        var token = BuildToken(
            JwtTestConfiguration.Issuer,
            JwtTestConfiguration.Audience,
            JwtTestConfiguration.SigningKey,
            expires: now.AddMinutes(-1),
            notBefore: now.AddMinutes(-10));

        await AssertUnauthorizedAsync(token);
    }

    [Fact]
    public async Task Jwt_WithWrongIssuer_ReturnsUnauthorized()
    {
        var token = BuildToken(
            "attacker-issuer",
            JwtTestConfiguration.Audience,
            JwtTestConfiguration.SigningKey,
            DateTime.UtcNow.AddMinutes(10));

        await AssertUnauthorizedAsync(token);
    }

    [Fact]
    public async Task Jwt_WithWrongAudience_ReturnsUnauthorized()
    {
        var token = BuildToken(
            JwtTestConfiguration.Issuer,
            "attacker-audience",
            JwtTestConfiguration.SigningKey,
            DateTime.UtcNow.AddMinutes(10));

        await AssertUnauthorizedAsync(token);
    }

    [Fact]
    public async Task Jwt_WithAlgorithmNone_ReturnsUnauthorized()
    {
        static string Encode(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var token =
            $"{Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}." +
            $"{Encode($"{{\"iss\":\"{JwtTestConfiguration.Issuer}\",\"aud\":\"{JwtTestConfiguration.Audience}\",\"exp\":{exp},\"role\":\"Admin\",\"nameid\":\"1\"}}")}.";

        await AssertUnauthorizedAsync(token);
    }

    [Fact]
    public async Task Login_Anonymous_IsAllowed()
    {
        var user = await fixture.SeedUserAsync($"anon-{Guid.NewGuid():N}@example.com", ValidPassword);
        using var client = fixture.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequestDto { Email = user.Email, Password = ValidPassword });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        login!.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Login_AsInactiveUser_ReturnsUnauthorized()
    {
        var user = await fixture.SeedUserAsync(
            $"inactive-sec-{Guid.NewGuid():N}@example.com",
            ValidPassword,
            isActive: false);
        using var client = fixture.CreateAnonymousClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequestDto { Email = user.Email, Password = ValidPassword });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Seller_CanRegisterSales()
    {
        var seeded = await fixture.SeedProductsAsync(5);
        fixture.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));

        using var response = await fixture.PostSaleAsync(
            new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Admin_CanRegisterInventoryEntries()
    {
        var seeded = await fixture.SeedProductsAsync(5);
        fixture.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/inventory/entries",
            new { productId = seeded.ProductIds[0], quantity = 3, reason = "Admin entry security test" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InventoryEntry_WithSpoofedUserId_UsesJwtNameIdentifier()
    {
        var seeded = await fixture.SeedProductsAsync(5);
        fixture.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/inventory/entries",
            new
            {
                productId = seeded.ProductIds[0],
                userId = int.MaxValue,
                quantity = 2,
                reason = "Spoofing security test"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var context = fixture.CreateDbContext();
        var movement = await context.StockMovements.SingleAsync(
            m => m.ProductId == seeded.ProductIds[0] && m.Reason == "Spoofing security test");
        movement.UserId.Should().Be(seeded.UserId);
    }

    [Fact]
    public async Task Sale_WithSpoofedUserId_UsesJwtNameIdentifier()
    {
        var seeded = await fixture.SeedProductsAsync(5);
        fixture.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));

        using var response = await fixture.PostSaleAsync(
            new
            {
                userId = int.MaxValue,
                items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } }
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var context = fixture.CreateDbContext();
        (await context.Sales.AnyAsync(s => s.UserId == int.MaxValue)).Should().BeFalse();
        (await context.Sales.AnyAsync(s => s.UserId == seeded.UserId)).Should().BeTrue();
    }

    [Fact]
    public void AllControllerActions_AreProtected_ExceptExplicitAnonymousAllowlist()
    {
        var allowedAnonymous = new HashSet<string>
        {
            "AuthController.Login",
            "HealthController.Get"
        };

        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        var unprotected = new List<string>();
        foreach (var controller in controllers)
        {
            var controllerAuthorized = controller.GetCustomAttributes<AuthorizeAttribute>(true).Any();
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any());

            foreach (var action in actions)
            {
                var name = $"{controller.Name}.{action.Name}";
                var anonymous = action.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
                var protectedAction = !anonymous
                    && (controllerAuthorized || action.GetCustomAttributes<AuthorizeAttribute>(true).Any());

                if (!protectedAction && !allowedAnonymous.Contains(name))
                {
                    unprotected.Add(name);
                }
            }
        }

        unprotected.Should().BeEmpty("every sensitive route must require authentication");
    }

    private async Task AssertUnauthorizedAsync(string token)
    {
        using var response = await SendAsync(
            "GET",
            "/api/categories",
            new AuthenticationHeaderValue("Bearer", token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string method,
        string path,
        AuthenticationHeaderValue? authorization)
    {
        using var client = fixture.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = authorization;
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        return await client.SendAsync(request);
    }

    private static string BuildToken(
        string issuer,
        string audience,
        string signingKey,
        DateTime expires,
        DateTime? notBefore = null)
    {
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Role, nameof(UserRole.Admin))
            ],
            notBefore ?? DateTime.UtcNow.AddSeconds(-1),
            expires,
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
