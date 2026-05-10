using Microsoft.AspNetCore.Mvc;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales.ListSales;

/// <summary>
/// Query-string parameters for GET /api/sales. Field names use the
/// '_'-prefixed convention from .doc/general-api.md (e.g. _page, _size,
/// _order, _minDate) so the API stays consistent with the rest of the
/// catalog.
/// </summary>
public class ListSalesRequest
{
    [FromQuery(Name = "_page")]
    public int Page { get; set; } = 1;

    [FromQuery(Name = "_size")]
    public int Size { get; set; } = 10;

    [FromQuery(Name = "_order")]
    public string? Order { get; set; }

    [FromQuery(Name = "saleNumber")]
    public string? SaleNumber { get; set; }

    [FromQuery(Name = "_minDate")]
    public DateTime? FromDate { get; set; }

    [FromQuery(Name = "_maxDate")]
    public DateTime? ToDate { get; set; }

    [FromQuery(Name = "customerId")]
    public Guid? CustomerId { get; set; }

    [FromQuery(Name = "branchId")]
    public Guid? BranchId { get; set; }

    [FromQuery(Name = "isCancelled")]
    public bool? IsCancelled { get; set; }
}
