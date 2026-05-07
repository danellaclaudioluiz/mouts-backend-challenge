using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

/// <summary>
/// Paginated, filtered, ordered list of sales. Mirrors the conventions in
/// .doc/general-api.md (page/size/order plus per-field filters).
/// </summary>
public class ListSalesQuery : IRequest<ListSalesResult>
{
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 10;
    public string? Order { get; set; }

    public string? SaleNumber { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? BranchId { get; set; }
    public bool? IsCancelled { get; set; }
}
