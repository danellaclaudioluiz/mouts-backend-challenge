using Ambev.DeveloperEvaluation.Domain.Entities;

namespace Ambev.DeveloperEvaluation.Domain.Repositories;

/// <summary>
/// Persistence contract for the Sale aggregate.
/// </summary>
public interface ISaleRepository
{
    /// <summary>Persists a new sale and returns it.</summary>
    Task<Sale> CreateAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>Persists changes to an existing sale.</summary>
    Task UpdateAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>Loads a sale with its items, or null if not found.</summary>
    Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Loads a sale by its business sale number, or null if not found.</summary>
    Task<Sale?> GetBySaleNumberAsync(string saleNumber, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a sale and its items. Returns false when not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sales matching the supplied filter, paged. Returns the page items
    /// alongside the total matching row count for the caller to compute paging
    /// metadata.
    /// </summary>
    Task<(IReadOnlyList<Sale> Items, int TotalCount)> ListAsync(
        SaleListFilter filter,
        CancellationToken cancellationToken = default);
}
