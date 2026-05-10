using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public HealthEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "GET /health/live returns 200 with status=Healthy and the Liveness probe entry")]
    public async Task Liveness_IsHealthy()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = (await response.Content.ReadFromJsonAsync<HealthReport>())!;
        report.Status.Should().Be("Healthy");
        report.HealthChecks.Should().Contain(c => c.Name == "Liveness" && c.Status == "Healthy");
        // Liveness must NOT carry the DB probe — that would defeat the
        // point of a liveness probe (which fires before the DB is reachable).
        report.HealthChecks.Should().NotContain(c => c.Name == "Postgres");
    }

    [Fact(DisplayName = "GET /health/ready returns 200 with the Postgres probe Healthy")]
    public async Task Readiness_IsHealthy_WithDbProbe()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = (await response.Content.ReadFromJsonAsync<HealthReport>())!;
        report.Status.Should().Be("Healthy");
        report.HealthChecks.Should().Contain(c => c.Name == "Postgres" && c.Status == "Healthy",
            "the readiness probe must include the AddDbContextCheck<DefaultContext> result so kubernetes withholds traffic when the DB is unreachable");
    }

    [Fact(DisplayName = "GET /health returns the aggregate report with every probe")]
    public async Task Health_Aggregate_ReturnsFullReport()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = (await response.Content.ReadFromJsonAsync<HealthReport>())!;
        report.Status.Should().Be("Healthy");
        report.HealthChecks.Should().Contain(c => c.Name == "Liveness");
        report.HealthChecks.Should().Contain(c => c.Name == "Readiness");
        report.HealthChecks.Should().Contain(c => c.Name == "Postgres");
    }

    private sealed record HealthReport(
        string Status,
        IReadOnlyList<HealthEntry> HealthChecks);

    private sealed record HealthEntry(
        string Name,
        string Status,
        string? Description,
        string? ErrorMessage,
        string? HostEnvironment);
}

/// <summary>
/// Owns its OWN Testcontainers Postgres + WebApi host so the rest of the
/// suite never sees the stopped container. The DB-offline scenario stops
/// this private container — Testcontainers reassigns a fresh host port on
/// restart, which would otherwise break every other test by leaving the
/// shared factory pointing at a dead port.
/// </summary>
[CollectionDefinition(DegradedHealthCollection.Name, DisableParallelization = true)]
public class DegradedHealthCollection : ICollectionFixture<SalesApiFactory>
{
    public const string Name = "degraded-health";
}

[Collection(DegradedHealthCollection.Name)]
public class HealthEndpointDegradedTests
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public HealthEndpointDegradedTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact(DisplayName = "GET /health/ready returns 503 with the Postgres probe Unhealthy when the DB is down")]
    public async Task Readiness_PostgresDown_Returns503()
    {
        // Confirm the probe is green BEFORE we stop the container, so the
        // 503 below is conclusively attributable to the outage rather than
        // a pre-existing config issue.
        var beforeStop = await _client.GetAsync("/health/ready");
        beforeStop.StatusCode.Should().Be(HttpStatusCode.OK);

        await _factory.StopDatabaseAsync();

        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the contract of AddDbContextCheck<DefaultContext> is to flip the probe to 503 when the DB is unreachable so kubernetes routes traffic away");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        var checks = doc.RootElement.GetProperty("healthChecks").EnumerateArray()
            .Select(e => new
            {
                Name = e.GetProperty("name").GetString(),
                Status = e.GetProperty("status").GetString(),
            }).ToList();
        checks.Should().Contain(c => c.Name == "Postgres" && c.Status == "Unhealthy");

        // Deliberately don't restart — the factory's DisposeAsync will clean
        // up the testcontainer at the end of the collection. Restarting
        // would just reassign a new host port the WebApp can't reach.
    }
}
