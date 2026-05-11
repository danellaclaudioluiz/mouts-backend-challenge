namespace Ambev.DeveloperEvaluation.Domain.Repositories;

/// <summary>
/// Header-only projection of a Sale used by list endpoints. Avoids the
/// cartesian explosion that would happen if items were eagerly loaded for
/// every row of a paginated list.
/// </summary>
public sealed record SaleSummary(
    Guid Id,
    string SaleNumber,
    DateTime SaleDate,
    Guid CustomerId,
    string CustomerName,
    Guid BranchId,
    string BranchName,
    decimal TotalAmount,
    bool IsCancelled,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int ItemCount);
