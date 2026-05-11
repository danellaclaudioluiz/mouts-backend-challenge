using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Ambev.DeveloperEvaluation.ORM;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Factory variant that simulates a Production environment: Jwt:Issuer
/// and Jwt:Audience are set, and the AuthenticationExtension enforces
/// ValidateIssuer / ValidateAudience. The shared SalesApiFactory runs
/// under "Test" environment where iss/aud are optional, which once
/// hid a producer/validator mismatch (the generator forgot to emit
/// iss/aud → 100% of authenticated requests returned 401 in prod).
/// </summary>
public class ProductionParityApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("sales_prod_parity")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public const string ExpectedIssuer = "https://sales-api.test.local";
    public const string ExpectedAudience = "sales-api-clients";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__SecretKey", "production-parity-test-jwt-secret-must-be-32-bytes");
        Environment.SetEnvironmentVariable("Jwt__Issuer", ExpectedIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", ExpectedAudience);
        Environment.SetEnvironmentVariable("RateLimit__PermitLimit", "10000");
        Environment.SetEnvironmentVariable("RateLimit__WindowSeconds", "60");

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        await context.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Staging" is treated by AuthenticationExtension exactly like
        // Production (only Development and Test get the iss/aud opt-out).
        builder.UseEnvironment("Staging");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["Jwt:SecretKey"] = "production-parity-test-jwt-secret-must-be-32-bytes",
                ["Jwt:Issuer"] = ExpectedIssuer,
                ["Jwt:Audience"] = ExpectedAudience,
                ["RateLimit:PermitLimit"] = "10000",
                ["RateLimit:WindowSeconds"] = "60"
            });
        });

        builder.ConfigureServices(services =>
        {
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<DefaultContext>));
            if (existing is not null) services.Remove(existing);

            services.AddDbContext<DefaultContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString(),
                    b => b.MigrationsAssembly("Ambev.DeveloperEvaluation.ORM")));
        });
    }
}

[CollectionDefinition(ProductionParityCollection.Name, DisableParallelization = true)]
public class ProductionParityCollection : ICollectionFixture<ProductionParityApiFactory>
{
    public const string Name = "production-parity";
}

/// <summary>
/// Guards CN-2: in a Staging/Production-like environment with
/// Jwt:Issuer and Jwt:Audience set, the token JwtTokenGenerator emits
/// MUST be accepted by JwtBearerHandler.ValidateToken. If the generator
/// forgets to populate the descriptor's Issuer / Audience, every login
/// here returns a token that the very next request rejects with 401 —
/// which is exactly the bug this test exists to catch.
/// </summary>
[Collection(ProductionParityCollection.Name)]
public class JwtProductionParityTests
{
    private readonly ProductionParityApiFactory _factory;
    private readonly HttpClient _client;

    public JwtProductionParityTests(ProductionParityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact(DisplayName = "Token minted with Jwt:Issuer/Audience set → carries iss/aud claims (CN-2 regression guard)")]
    public void GeneratedToken_CarriesIssuerAndAudience()
    {
        using var scope = _factory.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "prod-parity",
            Email = "prod@parity.test",
            Role = UserRole.Customer,
            Status = UserStatus.Active
        };

        var rawToken = generator.GenerateToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(rawToken);

        jwt.Issuer.Should().Be(ProductionParityApiFactory.ExpectedIssuer,
            "the generator must populate the iss claim — production validator rejects tokens without it");
        jwt.Audiences.Should().Contain(ProductionParityApiFactory.ExpectedAudience,
            "the generator must populate the aud claim — production validator rejects tokens without it");
        jwt.Claims.Should().Contain(c => c.Type == "jti", "every token needs a jti for revocation / log correlation");
    }

    [Fact(DisplayName = "End-to-end: signup → login → authenticated GET against a Production-like host")]
    public async Task SignupLogin_AuthenticatedRoundtrip_WorksUnderStaging()
    {
        var email = $"prod-parity-{Guid.NewGuid():N}@example.com";

        // 1. Signup (AllowAnonymous).
        var signup = await _client.PostAsJsonAsync("/api/v1/users", new
        {
            username = $"u{Guid.NewGuid():N}".Substring(0, 20),
            password = "Str0ngP@ssword!",
            phone = "+5511999998888",
            email
        });
        signup.StatusCode.Should().Be(HttpStatusCode.Created);

        // 2. Login (AllowAnonymous, auth-strict rate limit).
        var login = await _client.PostAsJsonAsync("/api/v1/auth", new
        {
            email,
            password = "Str0ngP@ssword!"
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK,
            "the login itself must succeed under Production-like iss/aud config");

        using var loginDoc = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginDoc.RootElement.GetProperty("data").GetProperty("token").GetString();
        token.Should().NotBeNullOrEmpty();

        // 3. Use the token against a Sales-protected route. This is the
        //    request that flipped to 401 under the previous bug — the
        //    validator demanded iss/aud, the generator omitted them.
        var authedReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sales?_page=1&_size=1");
        authedReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var listResponse = await _client.SendAsync(authedReq);

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Production parity test failed — generator must mint tokens the validator accepts. Body: {await listResponse.Content.ReadAsStringAsync()}");
    }
}
