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
    /// </summary>
    public string? Order { get; init; }

    public string? SaleNumber { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? BranchId { get; init; }
    public bool? IsCancelled { get; init; }
}
