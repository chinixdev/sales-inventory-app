using System.Text.RegularExpressions;

namespace SalesInventory.Application.Outbox;

/// <summary>Convierte excepciones en un texto acotado y sin secretos (LastError, respuestas del monitor).</summary>
public static partial class OutboxErrorFormatter
{
    public const int MaxLength = 1000;

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token|api[_-]?key|authorization|bearer)\b\s*[=:]?\s*[^\s;,]+")]
    private static partial Regex SecretPattern();

    public static string Format(Exception exception) => Sanitize($"{exception.GetType().Name}: {exception.Message}");

    public static string Sanitize(string text, int maxLength = MaxLength)
    {
        var redacted = SecretPattern().Replace(text, "$1=[redacted]");
        var singleLine = redacted.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }
}
