namespace Ambev.DeveloperEvaluation.Domain.Repositories;

/// <summary>
/// Pagination, ordering and filtering parameters for the sales list query.
/// Mirrors the conventions in <c>.doc/general-api.md</c>.
/// </summary>
public sealed record SaleListFilter
{
    public int Page { get; init; } = 1;
    public int Size { get; init; } = 10;

    /// <summary>
    /// Comma-separated ordering expression like <c>"saleDate desc, totalAmount asc"</c>.
    /// </summary>
    public string? Order { get; init; }

    public string? SaleNumber { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? BranchId { get; init; }
    public bool? IsCancelled { get; init; }
}
