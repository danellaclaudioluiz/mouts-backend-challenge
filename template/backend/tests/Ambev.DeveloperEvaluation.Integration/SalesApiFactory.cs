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

        // Apply migrations against the freshly-started container before any
        // test runs.
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DefaultContext>();
        await context.Database.MigrateAsync();
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
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
