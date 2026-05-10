using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.ORM.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Ambev.DeveloperEvaluation.ORM.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISaleRepository"/>.
/// </summary>
public class SaleRepository : ISaleRepository
{
    // GetByIdAsync is the hottest write-path read: every Update / Cancel /
    // CancelItem hits it. Compiling the query once avoids EF Core's
    // per-call Expression tree reflection and shaves measurable CPU on the
    // critical path.
    private static readonly Func<DefaultContext, Guid, Task<Sale?>> _getByIdCompiled =
        EF.CompileAsyncQuery((DefaultContext ctx, Guid id) =>
            ctx.Sales
                .Include(s => s.Items)
                .FirstOrDefault(s => s.Id == id));

    private readonly DefaultContext _context;

    public SaleRepository(DefaultContext context)
    {
        _context = context;
    }

    public async Task<Sale> CreateAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        await _context.Sales.AddAsync(sale, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return sale;
    }

    public Task UpdateAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        // The aggregate was loaded by GetByIdAsync, so EF Core is already
        // tracking it and every item it owns. Calling Update() here would
        // force every property to Modified and break orphan-removal of items
        // that the handler removed via Sale.RemoveItem. Just SaveChanges and
        // let the change tracker emit the right INSERT / UPDATE / DELETE set.
        return _context.SaveChangesAsync(cancellationToken);
    }

    public Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _getByIdCompiled(_context, id);
    }

    public Task<bool> SaleNumberExistsAsync(string saleNumber, CancellationToken cancellationToken = default)
    {
        return _context.Sales
            .AsNoTracking()
            .AnyAsync(s => s.SaleNumber == saleNumber, cancellationToken);
    }

    public Task<Sale?> GetBySaleNumberAsync(string saleNumber, CancellationToken cancellationToken = default)
    {
        return _context.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.SaleNumber == saleNumber, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sale = await _context.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (sale is null) return false;

        _context.Sales.Remove(sale);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<SalePage> ListAsync(
        SaleListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = ApplyFilters(_context.Sales.AsNoTracking().AsQueryable(), filter);
        var size = filter.Size < 1 ? 10 : filter.Size;

        return filter.Cursor is null
            ? await ListByOffsetAsync(query, filter, size, cancellationToken)
            : await ListByCursorAsync(query, filter, size, cancellationToken);
    }

    private static IQueryable<Sale> ApplyFilters(IQueryable<Sale> query, SaleListFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.SaleNumber))
        {
            var pattern = filter.SaleNumber.Replace('*', '%');
            query = pattern.Contains('%')
                ? query.Where(s => EF.Functions.ILike(s.SaleNumber, pattern))
                : query.Where(s => s.SaleNumber == filter.SaleNumber);
        }

        if (filter.FromDate.HasValue)
            query = query.Where(s => s.SaleDate >= filter.FromDate.Value);

        if (filter.ToDate.HasValue)
            query = query.Where(s => s.SaleDate <= filter.ToDate.Value);

        if (filter.CustomerId.HasValue)
            query = query.Where(s => s.CustomerId == filter.CustomerId.Value);

        if (filter.BranchId.HasValue)
            query = query.Where(s => s.BranchId == filter.BranchId.Value);

        if (filter.IsCancelled.HasValue)
            query = query.Where(s => s.IsCancelled == filter.IsCancelled.Value);

        return query;
    }

    private static async Task<SalePage> ListByOffsetAsync(
        IQueryable<Sale> query, SaleListFilter filter, int size, CancellationToken cancellationToken)
    {
        var totalCount = await query.LongCountAsync(cancellationToken);

        var orderedQuery = string.IsNullOrWhiteSpace(filter.Order)
            ? query.OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.Id)
            : query.OrderByDynamic(filter.Order, SaleListFilter.SupportedSortFields);

        var page = filter.Page < 1 ? 1 : filter.Page;

        var items = await ProjectToSummary(orderedQuery
            .Skip((page - 1) * size)
            .Take(size))
            .ToListAsync(cancellationToken);

        return new SalePage(items, totalCount, NextCursor: null);
    }

    private static async Task<SalePage> ListByCursorAsync(
        IQueryable<Sale> query, SaleListFilter filter, int size, CancellationToken cancellationToken)
    {
        // Keyset pagination: stable ordering by (SaleDate DESC, Id DESC) and
        // a composite WHERE that picks up exactly where the previous page
        // left off — index-friendly, O(log n) per page, no COUNT(*).
        var (cursorDate, cursorId) = SaleCursor.Decode(filter.Cursor!);

        var keysetQuery = query
            .Where(s => s.SaleDate < cursorDate
                        || (s.SaleDate == cursorDate && s.Id.CompareTo(cursorId) < 0))
            .OrderByDescending(s => s.SaleDate)
            .ThenByDescending(s => s.Id);

        // Fetch one extra row so we can tell whether a next page exists
        // without a separate count.
        var rows = await ProjectToSummary(keysetQuery.Take(size + 1))
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > size)
        {
            var last = rows[size - 1];
            nextCursor = SaleCursor.Encode(last.SaleDate, last.Id);
            rows.RemoveAt(size);
        }

        return new SalePage(rows, TotalCount: null, nextCursor);
    }

    private static IQueryable<SaleSummary> ProjectToSummary(IQueryable<Sale> source) =>
        source.Select(s => new SaleSummary(
            s.Id,
            s.SaleNumber,
            s.SaleDate,
            s.CustomerId,
            s.CustomerName,
            s.BranchId,
            s.BranchName,
            s.TotalAmount,
            s.IsCancelled,
            s.CreatedAt,
            s.UpdatedAt,
            s.ActiveItemsCount));
}
