using System.Net;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

[Collection(IntegrationCollection.Name)]
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly HttpClient _client;

    public HealthEndpointTests(SalesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "GET /health/live returns 200 with a Healthy body")]
    public async Task Liveness_IsHealthy()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Healthy");
    }

    [Fact(DisplayName = "GET /health/ready returns 200 with the DB probe included")]
    public async Task Readiness_IsHealthy_WithDbProbe()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Postgres",
            "the readiness endpoint must include the AddDbContextCheck<DefaultContext> result");
    }

    [Fact(DisplayName = "GET /health returns the full report")]
    public async Task Health_Aggregate_ReturnsFullReport()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Healthy");
        body.Should().Contain("Postgres");
    }
}
