using Ambev.DeveloperEvaluation.Application.Sales.Common;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

public class ListSalesResult
{
    public IReadOnlyList<SaleDto> Items { get; set; } = Array.Empty<SaleDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public int TotalPages => Size <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)Size);
}
