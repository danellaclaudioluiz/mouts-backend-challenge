namespace Ambev.DeveloperEvaluation.Application.Auth.RefreshToken;

public sealed class RefreshTokenResult
{
    public string Token { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiresAt { get; set; }
}
