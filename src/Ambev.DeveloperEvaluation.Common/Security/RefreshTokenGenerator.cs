using System.Security.Cryptography;
using System.Text;

namespace Ambev.DeveloperEvaluation.Common.Security;

/// <summary>
/// 256-bit RandomNumberGenerator → URL-safe Base64 raw token,
/// SHA-256 → hex digest for storage. Hashing means a stolen DB dump
/// reveals nothing usable for authentication.
/// </summary>
public sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private const int RawTokenBytes = 32; // 256 bits

    public (string RawToken, string Hash) Generate()
    {
        Span<byte> buffer = stackalloc byte[RawTokenBytes];
        RandomNumberGenerator.Fill(buffer);
        // URL-safe Base64: no '+' '/' '=' so the token can travel as a
        // bare header value without further encoding.
        var raw = Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return (raw, Hash(raw));
    }

    public string Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken), digest);
        var sb = new StringBuilder(SHA256.HashSizeInBytes * 2);
        foreach (var b in digest)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
