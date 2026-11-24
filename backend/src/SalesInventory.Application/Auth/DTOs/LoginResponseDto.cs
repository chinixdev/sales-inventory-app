namespace SalesInventory.Application.Auth.DTOs;

/// <summary>Bearer access token returned after successful authentication.</summary>
public sealed record LoginResponseDto(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAt);
