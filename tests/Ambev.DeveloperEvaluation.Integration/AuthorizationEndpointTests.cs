using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Locks in the authorization fallback: every non-anonymous endpoint must
/// reject anonymous traffic with 401. Health probes and the
/// login/signup endpoints opt out via <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/>
/// and must keep working without a token.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthorizationEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _anonymous;
    private readonly HttpClient _authenticated;

    public AuthorizationEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateAnonymousClient();
        _authenticated = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory(DisplayName = "Anonymous calls to non-anonymous Sales endpoints get 401")]
    [InlineData("GET", "/api/v1/sales")]
    [InlineData("POST", "/api/v1/sales")]
    [InlineData("GET", "/api/v1/sales/00000000-0000-0000-0000-000000000000")]
    [InlineData("PUT", "/api/v1/sales/00000000-0000-0000-0000-000000000000")]
    [InlineData("DELETE", "/api/v1/sales/00000000-0000-0000-0000-000000000000")]
    [InlineData("PATCH", "/api/v1/sales/00000000-0000-0000-0000-000000000000/cancel")]
    [InlineData("PATCH", "/api/v1/sales/00000000-0000-0000-0000-000000000000/items/00000000-0000-0000-0000-000000000000/cancel")]
    public async Task Anonymous_OnProtectedRoute_Returns401(string method, string path)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            req.Content = JsonContent.Create(PayloadBuilder.BuildCreate($"S-{Guid.NewGuid():N}"));
        if (method == "PUT")
            req.Content = JsonContent.Create(PayloadBuilder.BuildUpdate(Guid.NewGuid()));

        var response = await _anonymous.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"the AuthorizationFallbackPolicy must turn {method} {path} into a 401 when no bearer token is present");
    }

    [Fact(DisplayName = "Anonymous /api/v1/auth login bypasses the auth wall and reaches the handler")]
    public async Task Anonymous_AuthLogin_NotBlockedByFallbackPolicy()
    {
        // Well-formed payload that the handler rejects (unknown email) →
        // 401 Invalid credentials from the handler, NOT 401 from the
        // global fallback policy. The distinction: a "fallback 401" would
        // mean AllowAnonymous wasn't honoured at all, which is the bug
        // this test guards against.
        var response = await _anonymous.PostAsJsonAsync("/api/v1/auth", new
        {
            email = "nobody@example.com",
            password = "Wr0ngP@ssw0rd"
        });

        // Either way the body reached the controller — proven by the
        // problem+json shape (the fallback policy would emit an empty 401).
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "the controller / handler must respond — a fallback 401 would have no body");
    }

    [Fact(DisplayName = "Anonymous self-service POST /api/v1/users is allowed")]
    public async Task Anonymous_CreateUser_IsReachable()
    {
        // Valid payload — the public sign-up flow must work without a token.
        var response = await _anonymous.PostAsJsonAsync("/api/v1/users", new
        {
            username = $"u-{Guid.NewGuid():N}".Substring(0, 20),
            password = "Str0ngP@ssword!",
            phone = "+5511999999999",
            email = $"{Guid.NewGuid():N}@x.com"
        });

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "self-service signup MUST stay anonymous — there is no user yet to authenticate");
        ((int)response.StatusCode).Should().BeInRange(200, 299,
            "the happy-path payload should create a user (status in 2xx)");
    }

    [Fact(DisplayName = "Anonymous health endpoints stay open for kubernetes-style probes")]
    public async Task Anonymous_HealthEndpoints_Reachable()
    {
        (await _anonymous.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _anonymous.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _anonymous.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Mass assignment defense: POST /users with role=Admin smuggled in still creates a Customer")]
    public async Task CreateUser_AttemptsRoleEscalation_FallsBackToCustomer()
    {
        var email = $"{Guid.NewGuid():N}@x.com";

        // Body carries 'role' and 'status' fields the DTO does NOT declare.
        // System.Text.Json ignores unknown fields by default, so the values
        // never reach the command — defence in depth means even if the DTO
        // ever re-introduced them, the handler still hard-codes Customer.
        var response = await _anonymous.PostAsJsonAsync("/api/v1/users", new
        {
            username = $"u{Guid.NewGuid():N}".Substring(0, 20),
            password = "Str0ngP@ssword!",
            phone = "+5511999999999",
            email,
            role = "Admin",
            status = "Active"
        });
        response.EnsureSuccessStatusCode();

        // Read the user back via the authenticated client and confirm the
        // role landed as Customer regardless of what the body asked for.
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var createdId = doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var get = await _authenticated.GetAsync($"/api/v1/users/{createdId}");
        get.EnsureSuccessStatusCode();
        var getBody = await get.Content.ReadAsStringAsync();
        using var getDoc = System.Text.Json.JsonDocument.Parse(getBody);
        getDoc.RootElement.GetProperty("data").GetProperty("role").GetString()
            .Should().Be("Customer", "mass-assignment must not be able to escalate role on signup");
    }
}
