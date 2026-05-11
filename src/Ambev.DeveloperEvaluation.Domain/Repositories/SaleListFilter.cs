namespace Ambev.DeveloperEvaluation.Domain.Repositories;

/// <summary>
/// Pagination, ordering and filtering parameters for the sales list query.
/// Mirrors the conventions in <c>.doc/general-api.md</c>.
/// </summary>
public sealed record SaleListFilter
{
    /// <summary>
    /// Sale fields that may be used in the ordering expression. Validators
    /// check requested field names against this list before the query
    /// reaches the repository, so unknown columns produce a 400 instead of
    /// hitting the SQL layer.
    /// </summary>
    public static readonly IReadOnlyCollection<string> SupportedSortFields = new[]
    {
        "SaleNumber",
        "SaleDate",
        "TotalAmount",
        "IsCancelled",
        "CreatedAt",
        "UpdatedAt"
    };

    public int Page { get; init; } = 1;
    public int Size { get; init; } = 10;

    /// <summary>
    /// Comma-separated ordering expression like <c>"saleDate desc, totalAmount asc"</c>.
    /// Ignored in keyset (cursor) mode — that mode forces ordering by
    /// (SaleDate DESC, Id DESC) so the cursor stays stable.
    /// </summary>
    public string? Order { get; init; }

    /// <summary>
    /// Opaque cursor returned by the previous page. When set, the repository
    /// uses keyset pagination (O(log n) per page, no COUNT(*) round-trip)
    /// instead of LIMIT/OFFSET with a total count.
    /// </summary>
    public string? Cursor { get; init; }

    public string? SaleNumber { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? BranchId { get; init; }
    public bool? IsCancelled { get; init; }
}

/// <summary>
/// Result envelope returned by <see cref="ISaleRepository.ListAsync"/>.
/// In page-based mode <see cref="TotalCount"/> is populated; in keyset
/// (cursor) mode <see cref="NextCursor"/> is populated instead and
/// TotalCount is null because no COUNT(*) was run.
/// </summary>
public sealed record SalePage(
    IReadOnlyList<SaleSummary> Items,
    long? TotalCount,
    string? NextCursor);
