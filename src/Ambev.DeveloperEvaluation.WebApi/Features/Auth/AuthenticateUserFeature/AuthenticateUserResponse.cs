using System;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Auth.AuthenticateUserFeature;

/// <summary>
/// Represents the response returned after user authentication
/// </summary>
public sealed class AuthenticateUserResponse
{
    /// <summary>
    /// Gets or sets the JWT token for authenticated user
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's email address
    /// </summary>
    public string Email { get; set; } = string.Empty;   

    /// <summary>
    /// Gets or sets the user's full name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's role in the system
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Opaque rotation token. Pair with the JWT during the next
    /// <c>POST /auth/refresh</c> once the access token expires.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// UTC expiry of <see cref="RefreshToken"/>.
    /// </summary>
    public DateTime RefreshTokenExpiresAt { get; set; }
}
