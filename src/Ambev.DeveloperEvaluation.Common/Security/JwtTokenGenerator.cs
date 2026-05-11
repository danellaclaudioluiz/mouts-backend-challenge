using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Ambev.DeveloperEvaluation.Common.Security;

/// <summary>
/// Implementation of JWT (JSON Web Token) generator.
/// </summary>
public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the JWT token generator.
    /// </summary>
    /// <param name="configuration">Application configuration containing the necessary keys for token generation.</param>
    public JwtTokenGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Generates a JWT token for a specific user.
    /// </summary>
    /// <param name="user">User for whom the token will be generated.</param>
    /// <returns>Valid JWT token as string.</returns>
    /// <remarks>
    /// The generated token includes the following claims:
    /// - NameIdentifier (User ID)
    /// - Name (Username)
    /// - Role (User role)
    /// 
    /// The token is valid for 8 hours from the moment of generation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when user or secret key is not provided.</exception>
    public string GenerateToken(IUser user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var secretKey = _configuration["Jwt:SecretKey"];
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        var key = Encoding.UTF8.GetBytes(secretKey);

        var claims = new[]
        {
           new Claim(ClaimTypes.NameIdentifier, user.Id),
           new Claim(ClaimTypes.Name, user.Username),
           new Claim(ClaimTypes.Role, user.Role),
           // jti = unique token id. Lets ops correlate a specific token
           // in access logs without leaking the principal id, and gives
           // a future jti-denylist a stable key to revoke against.
           new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
           new Claim(JwtRegisteredClaimNames.Iat,
               DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
               ClaimValueTypes.Integer64)
       };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(8),
            // Issuer / Audience are mandatory in production (the
            // validator throws on startup when they're unset outside
            // Development / Test). Emitting them here too is the
            // matching half of the pair — without these claims on the
            // token, JwtBearerHandler.ValidateToken rejects the token
            // with SecurityTokenInvalidIssuerException and the API
            // returns 401 for every authenticated request. Empty-string
            // fallback so test environments (where the validator does
            // not require iss/aud) still get a parseable token.
            Issuer = _configuration["Jwt:Issuer"] ?? string.Empty,
            Audience = _configuration["Jwt:Audience"] ?? string.Empty,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}