using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Ambev.DeveloperEvaluation.Integration.Helpers;

/// <summary>
/// Single place to enforce the RFC 7807 contract on every error response.
/// Verifies content type, status code, and the presence of the four
/// fields (<c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>) plus
/// the <c>instance</c> path. Per-test custom assertions can build on top
/// (e.g. inspect <c>errors</c> on a validation problem).
/// </summary>
public static class ProblemDetailsAsserter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string? expectedTitleContains = null)
    {
        response.StatusCode.Should().Be(expected,
            "the response body was: " + await response.Content.ReadAsStringAsync());

        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "every error in this API is a RFC 7807 problem document");

        var raw = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(raw, Json);

        doc.TryGetProperty("type", out var type).Should().BeTrue("'type' is required by RFC 7807");
        type.GetString().Should().NotBeNullOrWhiteSpace();

        doc.TryGetProperty("title", out var title).Should().BeTrue("'title' is required by RFC 7807");
        title.GetString().Should().NotBeNullOrWhiteSpace();

        doc.TryGetProperty("status", out var status).Should().BeTrue("'status' is required by RFC 7807");
        status.GetInt32().Should().Be((int)expected);

        doc.TryGetProperty("instance", out var instance).Should().BeTrue(
            "the instance is the request path — needed to correlate logs to user-visible errors");
        instance.GetString().Should().NotBeNullOrWhiteSpace();

        if (expectedTitleContains is not null)
            title.GetString().Should().Contain(expectedTitleContains);

        return doc;
    }
}
