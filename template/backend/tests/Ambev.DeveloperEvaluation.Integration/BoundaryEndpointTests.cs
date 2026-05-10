using System.Net;
using System.Net.Http.Json;
using Ambev.DeveloperEvaluation.Domain.Services;
using Ambev.DeveloperEvaluation.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Boundary-condition E2E tests: things the validators and aggregate guards
/// claim to enforce, asserted at the HTTP edge so a future refactor that
/// moves or removes those guards is caught by CI.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class BoundaryEndpointTests : IAsyncLifetime
{
    private readonly SalesApiFactory _factory;
    private readonly HttpClient _client;

    public BoundaryEndpointTests(SalesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = $"POST with exactly {nameof(SaleItemDiscountPolicy.MaxItemsPerSale)} items succeeds (lower boundary of rejection)")]
    public async Task CreateSale_AtItemCountLimit_Succeeds()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var items = Enumerable.Range(0, SaleItemDiscountPolicy.MaxItemsPerSale)
            .Select(_ => new
            {
                productId = Guid.NewGuid(),
                productName = "Beer",
                quantity = 1,
                unitPrice = 10m
            })
            .ToArray();

        var response = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber,
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = "Acme",
            branchId = Guid.NewGuid(),
            branchName = "Branch 1",
            items
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"exactly {SaleItemDiscountPolicy.MaxItemsPerSale} items is at the cap, not over it");
    }

    [Fact(DisplayName = $"POST with {nameof(SaleItemDiscountPolicy.MaxItemsPerSale)} + 1 items is rejected with 400")]
    public async Task CreateSale_OverItemCountLimit_Returns400()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var items = Enumerable.Range(0, SaleItemDiscountPolicy.MaxItemsPerSale + 1)
            .Select(_ => new
            {
                productId = Guid.NewGuid(),
                productName = "Beer",
                quantity = 1,
                unitPrice = 10m
            })
            .ToArray();

        var response = await _client.PostAsJsonAsync("/api/v1/sales", new
        {
            saleNumber,
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = "Acme",
            branchId = Guid.NewGuid(),
            branchName = "Branch 1",
            items
        });

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "POST with duplicate ProductId across lines is rejected (cap cannot be split)")]
    public async Task CreateSale_DuplicateProductIds_Returns400()
    {
        var saleNumber = $"S-{Guid.NewGuid():N}";
        var sharedProductId = Guid.NewGuid();
        var payload = new
        {
            saleNumber,
            saleDate = DateTime.UtcNow.AddMinutes(-1),
            customerId = Guid.NewGuid(),
            customerName = "Acme",
            branchId = Guid.NewGuid(),
            branchName = "Branch 1",
            items = new[]
            {
                new { productId = sharedProductId, productName = "Beer", quantity = 15, unitPrice = 10m },
                new { productId = sharedProductId, productName = "Beer", quantity = 10, unitPrice = 10m }
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/sales", payload);

        await ProblemDetailsAsserter.AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }
}
