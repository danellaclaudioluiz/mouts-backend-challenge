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

    /// <summary>
    /// Returns true when a sale with the supplied business number already
    /// exists. Used as a fast-path pre-check before insert; the unique index
    /// on SaleNumber is still the source of truth and a concurrent insert is
    /// translated to 409 by the WebApi exception middleware.
    /// </summary>
    Task<bool> SaleNumberExistsAsync(string saleNumber, CancellationToken cancellationToken = default);

    /// <summary>Loads a sale by its business sale number, or null if not found.</summary>
    Task<Sale?> GetBySaleNumberAsync(string saleNumber, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a sale and its items. Returns false when not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sales matching the supplied filter, paged. Returns the page items
    /// as header-only summaries (no SaleItem load) alongside the total matching
    /// row count for the caller to compute paging metadata.
    /// </summary>
    Task<(IReadOnlyList<SaleSummary> Items, long TotalCount)> ListAsync(
        SaleListFilter filter,
        CancellationToken cancellationToken = default);
}
