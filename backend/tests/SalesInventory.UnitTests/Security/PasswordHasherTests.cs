using FluentAssertions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Users;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Security;

namespace SalesInventory.UnitTests.Security;

public sealed class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new Pbkdf2PasswordHasher();

    [Fact]
    public void HashPassword_UsesUniqueSaltAndVerifiesCorrectPassword()
    {
        const string password = "DevPassword-12345";

        var firstHash = _hasher.HashPassword(password);
        var secondHash = _hasher.HashPassword(password);
        var user = new User
        {
            Email = "admin@salesinventory.test",
            PasswordHash = firstHash,
            IsActive = true
        };

        firstHash.Should().StartWith("PBKDF2-SHA256$");
        firstHash.Should().NotBe(password);
        secondHash.Should().NotBe(firstHash);
        _hasher.VerifyPassword(user, password).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WhenPasswordIsWrong_ReturnsFalse()
    {
        var user = CreateUser(_hasher.HashPassword("DevPassword-12345"));

        _hasher.VerifyPassword(user, "OtherPassword-12345").Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_WhenUserIsInactiveOrEmailIsInvalid_ReturnsFalse()
    {
        var hash = _hasher.HashPassword("DevPassword-12345");
        var inactiveUser = CreateUser(hash);
        inactiveUser.IsActive = false;
        var invalidEmailUser = CreateUser(hash);
        invalidEmailUser.Email = "not-an-email";

        _hasher.VerifyPassword(inactiveUser, "DevPassword-12345").Should().BeFalse();
        _hasher.VerifyPassword(invalidEmailUser, "DevPassword-12345").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("                         ")]
    public void HashPassword_WhenPasswordViolatesPolicy_Throws(string password)
    {
        var act = () => _hasher.HashPassword(password);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NormalizeEmail_ValidatesAndNormalizesAddress()
    {
        UserAccountValidation.NormalizeEmail("  Admin@SalesInventory.test ")
            .Should().Be("admin@salesinventory.test");
        UserAccountValidation.IsValidEmail("not-an-email").Should().BeFalse();
    }

    private static User CreateUser(string passwordHash) => new()
    {
        Email = "admin@salesinventory.test",
        PasswordHash = passwordHash,
        IsActive = true
    };
}
