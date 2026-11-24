using System.ComponentModel.DataAnnotations;

namespace SalesInventory.Application.Auth.DTOs;

/// <summary>Credentials submitted to obtain an access token.</summary>
public sealed record LoginRequestDto
{
    [Required]
    [EmailAddress]
    [StringLength(255)]
    public required string Email { get; init; }

    [Required]
    [StringLength(128)]
    public required string Password { get; init; }
}
