using Ambev.DeveloperEvaluation.WebApi.Features.Sales.CreateSale;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales.UpdateSale;

/// <summary>
/// HTTP body for PUT /api/sales/{id}. SaleNumber is immutable so it's not
/// part of the request — the route id identifies the sale instead.
/// </summary>
public class UpdateSaleRequest
{
    public DateTime SaleDate { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public List<CreateSaleItemRequest> Items { get; set; } = new();
}
