using Ambev.DeveloperEvaluation.Application.Sales.Common;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

public class ListSalesResult
{
    public IReadOnlyList<SaleSummaryDto> Items { get; set; } = Array.Empty<SaleSummaryDto>();

    /// <summary>
    /// Total matching rows in offset/page mode. <c>null</c> in cursor
    /// (keyset) mode — the keyset query does not run a COUNT(*).
    /// </summary>
    public long? TotalCount { get; set; }

    public int Page { get; set; }
    public int Size { get; set; }

    /// <summary>Opaque cursor for the next page (keyset mode only).</summary>
    public string? NextCursor { get; set; }

    public long? TotalPages =>
        TotalCount is null || Size <= 0 ? null : (long)Math.Ceiling(TotalCount.Value / (double)Size);
}
