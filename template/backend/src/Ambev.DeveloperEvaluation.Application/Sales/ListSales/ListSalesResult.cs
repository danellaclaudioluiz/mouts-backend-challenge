using Ambev.DeveloperEvaluation.Application.Sales.Common;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

public class ListSalesResult
{
    public IReadOnlyList<SaleSummaryDto> Items { get; set; } = Array.Empty<SaleSummaryDto>();
    public long TotalCount { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public long TotalPages => Size <= 0 ? 0 : (long)Math.Ceiling(TotalCount / (double)Size);
}
