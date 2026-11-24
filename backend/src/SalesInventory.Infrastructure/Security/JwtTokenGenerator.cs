using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SalesInventory.Application.Auth.DTOs;
using SalesInventory.Application.Auth.Interfaces;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Security;

public sealed class JwtTokenGenerator(IOptions<JwtOptions> options) : IJwtTokenGenerator
{
    private readonly JwtOptions _options = Validate(options.Value);

    public LoginResponseDto Generate(User user)
    {
        var issuedAt = DateTime.UtcNow;
        var expiresAt = issuedAt.AddMinutes(_options.ExpirationMinutes);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var credentials = new SigningCredentials(
            _options.CreateSigningKey(),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expiresAt,
            signingCredentials: credentials);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return new LoginResponseDto(
            accessToken,
            "Bearer",
            new DateTimeOffset(expiresAt, TimeSpan.Zero));
    }

    private static JwtOptions Validate(JwtOptions options)
    {
        options.Validate();
        return options;
    }
}
