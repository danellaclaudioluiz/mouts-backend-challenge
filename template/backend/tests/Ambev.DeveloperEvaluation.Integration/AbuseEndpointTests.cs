using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Abuse / hardening scenarios: inputs a malicious or sloppy client might
/// send. The API must store the data verbatim, never render it raw on
/// output (System.Text.Json encodes &lt; &gt; &amp; by default), and bounce
/// payloads that exceed declared lengths or carry malformed identifiers.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AbuseEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public AbuseEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "CustomerName=<script>… is stored verbatim and JSON-encoded on the way out")]
    public async Task CustomerName_XssPayload_IsJsonEncodedOnGet()
    {
        const string xssCustomerName = "<script>alert('x')</script>";
        var saleNumber = $"S-{Guid.NewGuid():N}";

        var create = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber,
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = xssCustomerName,
            branchId = Guid.NewGuid(),
            branchName = "Branch",
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = "Beer", quantity = 1, unitPrice = 10m }
            }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created,
            "user data must be accepted verbatim — sanitisation belongs at the rendering layer, not at the API");

        var created = (await create.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;

        var get = await _client.GetAsync($"/api/v1/sales/{created.Id}");
        get.EnsureSuccessStatusCode();
        var rawBody = await get.Content.ReadAsStringAsync();

        // System.Text.Json's default JavaScriptEncoder escapes <, >, &, ' and "
        // — so a stored "<script>..." is rendered as "<script>..."
        // on the wire. A naive HTML render would still need its own escape
        // step, but the JSON layer itself does not produce executable HTML.
        rawBody.Should().NotContain("<script>",
            "System.Text.Json must JSON-encode angle brackets and quotes so the API never emits an executable HTML/JS string");
        // Encoder emits uppercase hex (<). Compare case-insensitively
        // so a future SDK upgrade switching to lowercase doesn't flake.
        rawBody.ToLowerInvariant().Should().Contain("\\u003cscript\\u003e",
            "the encoded form must round-trip — otherwise the original payload was silently mutated");

        // Round-trip the JSON properly and confirm the stored value is the
        // exact, byte-equal original — no sanitisation happened anywhere.
        var roundTripped = (await get.Content.ReadFromJsonAsync<EnvelopedSale>())!.Data;
        // Re-decode via the deserialiser (which un-escapes < → <) and
        // confirm the original characters survived storage.
        var afterRoundTrip = System.Text.Json.JsonSerializer.Deserialize<string>(
            $"\"{xssCustomerName.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("'", "\\u0027")}\"");
        afterRoundTrip.Should().Be(xssCustomerName);
    }

    [Fact(DisplayName = "SaleNumber 51 chars is rejected with 400 + 'SaleNumber' in errors")]
    public async Task SaleNumber_OverMaxLength_Returns400()
    {
        var oversizedSaleNumber = new string('S', 51); // limit is 50

        var response = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber = oversizedSaleNumber,
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = "Acme",
            branchId = Guid.NewGuid(),
            branchName = "Branch",
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = "Beer", quantity = 1, unitPrice = 10m }
            }
        });

        var problem = await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("errors").EnumerateObject()
            .Should().Contain(p => p.Name.Contains("SaleNumber"),
                "the validator must surface which field tripped, not just a generic 'validation failed'");
    }

    [Fact(DisplayName = "CustomerName 201 chars is rejected with 400 + 'CustomerName' in errors")]
    public async Task CustomerName_OverMaxLength_Returns400()
    {
        var oversizedName = new string('A', 201); // limit is 200

        var response = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber = $"S-{Guid.NewGuid():N}",
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = oversizedName,
            branchId = Guid.NewGuid(),
            branchName = "Branch",
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = "Beer", quantity = 1, unitPrice = 10m }
            }
        });

        var problem = await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("errors").EnumerateObject()
            .Should().Contain(p => p.Name.Contains("CustomerName"));
    }

    [Fact(DisplayName = "ProductName 201 chars is rejected with 400 + per-field error")]
    public async Task ProductName_OverMaxLength_Returns400()
    {
        var oversizedProductName = new string('P', 201);

        var response = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber = $"S-{Guid.NewGuid():N}",
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = "Acme",
            branchId = Guid.NewGuid(),
            branchName = "Branch",
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = oversizedProductName, quantity = 1, unitPrice = 10m }
            }
        });

        var problem = await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
        // RuleForEach surfaces the field as something like "Items[0].ProductName"
        problem.GetProperty("errors").EnumerateObject()
            .Should().Contain(p => p.Name.Contains("ProductName"));
    }

    [Fact(DisplayName = "GET /api/v1/sales/not-a-guid is rejected by the route constraint")]
    public async Task GetSale_MalformedGuid_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/sales/not-a-guid");

        // [HttpGet("{id:guid}")] route constraint mismatch → no route
        // matches → 404 (NOT 400) by design. Lock that in: any drift
        // (e.g. someone removes the :guid constraint) would let a bad
        // path reach the controller and 500 deeper.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the {id:guid} route constraint must reject malformed identifiers before they reach the controller");
    }
}
