using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Enums;
using Ambev.DeveloperEvaluation.ORM;
using Ambev.DeveloperEvaluation.WebApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// xUnit fixture that boots a Postgres testcontainer and an in-process WebApi
/// host pointed at it. Migrations run once at start, and the database is torn
/// down with the container at the end of the test session.
/// </summary>
/// <remarks>
/// Requires a working Docker daemon on the test machine — that is the
/// canonical way to run integration tests locally and in CI without
/// stubbing out infrastructure.
///
/// Shared across every test class via <see cref="IntegrationCollection"/>, so
/// the container is booted once per test session, not per class. Tests in
/// the collection run sequentially (xUnit collection contract) so the
/// per-test <see cref="ResetDatabaseAsync"/> below is race-free.
/// </remarks>
public class SalesApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("sales_integration")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Program.Main reads ConnectionStrings:DefaultConnection at builder
        // construction time — BEFORE WebApplicationFactory's ConfigureWebHost
        // callbacks fire and merge the InMemoryCollection below. Setting the
        // env var here ensures the value is in the default configuration
        // sources (env vars are auto-loaded by WebApplication.CreateBuilder)
        // before the host is built on first Services access.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "Jwt__SecretKey", "integration-tests-jwt-secret-must-be-at-least-32-bytes-long");
        // Bursty integration suites trip the default 100-rpm limit when they
        // all share the loopback partition; bump it for the broad suite,
        // overrideable by subclasses that exercise the limiter on purpose.
        Environment.SetEnvironmentVariable("RateLimit__PermitLimit", "10000");
        Environment.SetEnvironmentVariable("RateLimit__WindowSeconds", "60");
        ConfigureExtraEnvironment();

        // Apply migrations against the freshly-started container before any
        // test runs.
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Hook for subclasses (e.g. <c>RateLimitedSalesApiFactory</c>) to
    /// inject extra environment variables that must be in place before the
    /// host is first built.
    /// </summary>
    protected virtual void ConfigureExtraEnvironment()
    {
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Truncates the mutable application tables so the next test starts
    /// against a known-empty state. Migrations history and the trigger
    /// functions stay in place. Called from each test class's IAsyncLifetime
    /// so every test sees a clean slate.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        await context.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE
                ""SaleItems"",
                ""OutboxMessages"",
                ""Sales"",
                ""Users""
            RESTART IDENTITY CASCADE;
        ");
    }

    /// <summary>
    /// Stops the underlying Postgres container without disposing the
    /// factory. Used by the DB-degraded health-check test — the next
    /// /health/ready probe must drop to 503. Pair every call with a
    /// matching <see cref="StartDatabaseAsync"/> in a finally block, or
    /// the rest of the suite will see a dead host.
    /// </summary>
    public Task StopDatabaseAsync() => _postgres.StopAsync();

    public Task StartDatabaseAsync() => _postgres.StartAsync();

    /// <summary>
    /// Mints a JWT for an Admin "integration test" principal and returns
    /// it as a raw bearer string. The user is NOT persisted — the API
    /// only validates the JWT signature/lifetime/issuer, never re-fetches
    /// the user by id. Production middleware that re-checks User.Status
    /// (planned, see security review H4) would also need a real DB row.
    /// </summary>
    public string MintBearerToken(string role = "Admin", Guid? userId = null)
    {
        using var scope = Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var user = User.Create(
            username: "integration-tests",
            passwordHash: "test-hash",
            email: "integration@tests.local",
            phone: "+5511999998888");
        user.Id = userId ?? Guid.NewGuid();
        var desiredRole = role switch
        {
            "Admin" => UserRole.Admin,
            "Manager" => UserRole.Manager,
            _ => UserRole.Customer
        };
        if (desiredRole != UserRole.Customer)
            user.ChangeRole(desiredRole);
        return generator.GenerateToken(user);
    }

    /// <summary>
    /// Every client created via <c>CreateClient()</c> is automatically
    /// authenticated as an Admin principal so existing tests don't need
    /// to thread a bearer token through each call. Tests that need to
    /// probe the anonymous path do so via <c>CreateAnonymousClient()</c>.
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintBearerToken());
    }

    /// <summary>
    /// Returns a client WITHOUT the default bearer header — used by tests
    /// that exercise the authentication / authorization boundary itself
    /// (anonymous → 401, anonymous-allowed endpoints → 200, etc.).
    /// </summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Kept in addition to the env var set in InitializeAsync so
                // anything that reads from IConfiguration AFTER builder.Build
                // (services, controllers) sees the same value.
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["Jwt:SecretKey"] = "integration-tests-jwt-secret-must-be-at-least-32-bytes-long",
                // Keep the rate limit out of the way of the broad integration
                // suite: hundreds of requests fired against loopback within a
                // single minute would otherwise share one partition and trip
                // the 100-rpm default. Tests that specifically exercise the
                // limiter spin up their own factory with a tighter PermitLimit.
                ["RateLimit:PermitLimit"] = "10000",
                ["RateLimit:WindowSeconds"] = "60"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the configured DefaultContext with one pointing at the
            // testcontainer; the rest of the registrations (repositories,
            // publisher, dispatcher, etc.) keep working unchanged.
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<DefaultContext>));
            if (existing is not null) services.Remove(existing);

            services.AddDbContext<DefaultContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString(),
                    b => b.MigrationsAssembly("Ambev.DeveloperEvaluation.ORM")));
        });
    }
}
