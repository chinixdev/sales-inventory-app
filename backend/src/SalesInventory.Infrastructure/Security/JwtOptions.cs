using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SalesInventory.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int DefaultExpirationMinutes = 30;
    private const int MinimumSigningKeyBytes = 32;
    private const int MaximumExpirationMinutes = 1440;

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SigningKey { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = DefaultExpirationMinutes;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer)
            || string.IsNullOrWhiteSpace(Audience)
            || string.IsNullOrWhiteSpace(SigningKey)
            || Encoding.UTF8.GetByteCount(SigningKey) < MinimumSigningKeyBytes
            || ExpirationMinutes is < 1 or > MaximumExpirationMinutes)
        {
            throw new InvalidOperationException(
                "JWT configuration is invalid. Configure a signing key of at least 256 bits, issuer, audience, and an expiration between 1 and 1440 minutes.");
        }
    }

    public SymmetricSecurityKey CreateSigningKey()
    {
        Validate();
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
    }
}
