using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Users;

public static class UserAccountValidation
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;
    private const int MaximumEmailLength = 255;

    public static string NormalizeEmail(string? email)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail)
            || normalizedEmail.Length > MaximumEmailLength
            || !new EmailAddressAttribute().IsValid(normalizedEmail))
        {
            throw new ArgumentException("Debe proporcionar un email válido.", nameof(email));
        }

        return normalizedEmail.ToLowerInvariant();
    }

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > MaximumEmailLength)
        {
            return false;
        }

        return new EmailAddressAttribute().IsValid(email);
    }

    public static void ValidatePassword(string? password)
    {
        if (!IsValidPassword(password))
        {
            throw new ArgumentException(
                $"La contraseña debe tener entre {MinimumPasswordLength} y {MaximumPasswordLength} caracteres.",
                nameof(password));
        }
    }

    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrWhiteSpace(password)
        && password.Length >= MinimumPasswordLength
        && password.Length <= MaximumPasswordLength;
}
