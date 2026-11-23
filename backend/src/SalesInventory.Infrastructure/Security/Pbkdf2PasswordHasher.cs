using System.Globalization;
using System.Security.Cryptography;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Users;
using SalesInventory.Domain.Entities;

namespace SalesInventory.Infrastructure.Security;

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MinimumAcceptedIterations = 100_000;
    private const int MaximumAcceptedIterations = 1_000_000;

    public string HashPassword(string password)
    {
        UserAccountValidation.ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Join(
            '$',
            Algorithm,
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool VerifyPassword(User user, string password)
    {
        if (user is null
            || !user.IsActive
            || !UserAccountValidation.IsValidEmail(user.Email)
            || !UserAccountValidation.IsValidPassword(password))
        {
            return false;
        }

        var parts = user.PasswordHash.Split('$');
        if (parts.Length != 4
            || parts[0] != Algorithm
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < MinimumAcceptedIterations
            || iterations > MaximumAcceptedIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltSize || expectedHash.Length != HashSize)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
