using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Ambev.DeveloperEvaluation.Common.Security
{
    public static class AuthenticationExtension
    {
        public static IServiceCollection AddJwtAuthentication(
            this IServiceCollection services,
            IConfiguration configuration,
            IHostEnvironment environment)
        {
            var secretKey = configuration["Jwt:SecretKey"];
            ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
            if (secretKey.Length < 32)
                throw new InvalidOperationException(
                    "Jwt:SecretKey must be at least 32 bytes — HS256 keys shorter than that are trivially brute-forceable.");

            var key = Encoding.UTF8.GetBytes(secretKey);

            var issuer = configuration["Jwt:Issuer"];
            var audience = configuration["Jwt:Audience"];

            // Outside development, Jwt:Issuer and Jwt:Audience are MANDATORY.
            // Without them, anyone holding the signing key can mint tokens
            // accepted by every service that shares it — even ones meant
            // for a different tenant / audience. Failing fast here beats
            // a silent "leaked key works everywhere" posture in prod.
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
            {
                if (string.IsNullOrWhiteSpace(issuer))
                    throw new InvalidOperationException(
                        "Jwt:Issuer is required outside Development — set it to the URL of this API so a leaked key can't be reused for another service.");
                if (string.IsNullOrWhiteSpace(audience))
                    throw new InvalidOperationException(
                        "Jwt:Audience is required outside Development — set it to the resource these tokens grant access to.");
            }

            services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                // Allow plain-HTTP token discovery only in dev — production
                // must refuse to fetch the JWKS metadata over HTTP.
                x.RequireHttpsMetadata = !environment.IsDevelopment();
                x.SaveToken = true;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
                    ValidIssuer = issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

            services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();

            return services;
        }
    }
}
