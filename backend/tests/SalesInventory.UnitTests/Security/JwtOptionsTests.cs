using FluentAssertions;
using SalesInventory.Infrastructure.Security;

namespace SalesInventory.UnitTests.Security;

public sealed class JwtOptionsTests
{
    [Fact]
    public void Validate_WhenRequiredConfigurationIsMissing_ThrowsWithoutRevealingKey()
    {
        var options = new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience"
        };

        Action act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JWT configuration is invalid*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Validate_WhenExpirationIsOutsideAllowedRange_Throws(int expirationMinutes)
    {
        var options = ValidOptions();
        options.ExpirationMinutes = expirationMinutes;

        Action act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_WhenConfigurationIsValid_DoesNotThrow()
    {
        Action act = () => ValidOptions().Validate();

        act.Should().NotThrow();
    }

    private static JwtOptions ValidOptions() => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = Convert.ToBase64String(new byte[32])
    };
}
