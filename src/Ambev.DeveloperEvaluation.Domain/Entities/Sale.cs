using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.ValueObjects;

namespace Ambev.DeveloperEvaluation.Domain.Entities;

/// <summary>
/// A sale aggregate root. Owns its collection of <see cref="SaleItem"/>s and
/// is the only place where item-level state can be mutated, so the 20-item
/// per-product cap and the discount tiers can be enforced as invariants.
/// </summary>
/// <remarks>
/// External identities pattern: Customer, Branch and Product are referenced
/// by id and a denormalized name only — no FK to other bounded contexts.
/// </remarks>
public class Sale : BaseEntity
{
    private readonly List<SaleItem> _items = new();
    private readonly List<IDomainEvent> _domainEvents = new();

    public string SaleNumber { get; private set; } = string.Empty;
    public DateTime SaleDate { get; private set; }
    public Guid CustomerId { get; private set; }
    public string CustomerName { get; private set; } = string.Empty;
    public Guid BranchId { get; private set; }
    public string BranchName { get; private set; } = string.Empty;
    public Money TotalAmount { get; private set; } = Money.Zero;
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Cached number of non-cancelled items on this sale. Maintained by the
    /// aggregate on every mutation so list-style queries can read the count
    /// from the row instead of running a correlated subquery against
    /// SaleItems for every page.
    /// </summary>
    public int ActiveItemsCount { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. A <c>bigint</c> column maintained by a
    /// Postgres BEFORE UPDATE trigger that increments the value on every row
    /// update. Clients can derive an HTTP ETag from it for If-Match
    /// preconditions on PUT/DELETE.
    /// </summary>
    /// <remarks>
    /// Was previously mapped to Postgres <c>xmin</c>. xmin gets reset by
    /// VACUUM FREEZE (when age(xmin) crosses vacuum_freeze_table_age, default
    /// 150M txns) — a rare but catastrophic event that would invalidate
    /// every cached ETag at once. The trigger-managed bigint is monotonic
    /// across the row's lifetime and is not affected by FREEZE.
    /// </remarks>
    public long RowVersion { get; private set; }

    public IReadOnlyCollection<SaleItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Required by EF Core. Do not call from domain code — use
    /// <see cref="Create"/> instead.
    /// </summary>
    private Sale()
    {
    }

    /// <summary>
    /// Creates a new sale with header data only. Use this overload only
    /// when the items will be added in a separate, controlled flow (tests,
    /// importers). Production callers — i.e. the CreateSale handler — use
    /// the items-aware overload below so <see cref="SaleCreatedEvent"/>
    /// fires atomically with construction and the aggregate is never
    /// observed in a half-built "no items yet" state by a downstream
    /// consumer.
    /// </summary>
    public static Sale Create(
        string saleNumber,
        DateTime saleDate,
        Guid customerId,
        string customerName,
        Guid branchId,
        string branchName)
    {
        ValidateHeader(saleNumber, customerId, customerName, branchId, branchName);

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            SaleNumber = saleNumber,
            SaleDate = saleDate,
            CustomerId = customerId,
            CustomerName = customerName,
            BranchId = branchId,
            BranchName = branchName,
            CreatedAt = DateTime.UtcNow,
            IsCancelled = false,
            TotalAmount = Money.Zero
        };

        return sale;
    }

    /// <summary>
    /// Item descriptor used by the items-aware <see cref="Create(string, DateTime, Guid, string, Guid, string, IEnumerable{Sale.NewItem})"/>
    /// factory below. A record so callers (typically handlers mapping from a
    /// command DTO) don't have to construct a SaleItem directly.
    /// </summary>
    public sealed record NewItem(Guid ProductId, string ProductName, int Quantity, decimal UnitPrice);

    /// <summary>
    /// Items-aware factory. Validates the header, walks every item through
    /// the same invariants <see cref="AddItem"/> enforces, and raises
    /// <see cref="SaleCreatedEvent"/> with the FINAL totals — encapsulating
    /// the previous two-step pattern (Create → AddItem* → MarkCreated)
    /// behind a single atomic call so callers can't accidentally publish
    /// a half-built aggregate.
    /// </summary>
    public static Sale Create(
        string saleNumber,
        DateTime saleDate,
        Guid customerId,
        string customerName,
        Guid branchId,
        string branchName,
        IEnumerable<NewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sale = Create(saleNumber, saleDate, customerId, customerName, branchId, branchName);
        foreach (var item in items)
            sale.AddItem(item.ProductId, item.ProductName, item.Quantity, item.UnitPrice);

        sale.AddDomainEvent(new SaleCreatedEvent(
            sale.Id, sale.SaleNumber, sale.CustomerId, sale.BranchId,
            sale.TotalAmount, sale._items.Count));

        return sale;
    }

    /// <summary>
    /// Adds a new line for a product. Each ProductId may appear at most once
    /// in a sale's non-cancelled items — callers must consolidate quantities
    /// before adding. The same rule applies in both Create and Update flows,
    /// so the API surface is consistent and the per-product 20-cap can't be
    /// bypassed by splitting lines.
    /// </summary>
    public SaleItem AddItem(Guid productId, string productName, int quantity, decimal unitPrice)
    {
        EnsureNotCancelled();

        if (_items.Any(i => i.ProductId == productId && !i.IsCancelled))
            throw new DomainException(
                $"Product '{productId}' is already in sale '{SaleNumber}'. " +
                "Consolidate quantities for the same product into a single line.");

        var item = new SaleItem(productId, productName, quantity, unitPrice)
        {
            SaleId = Id
        };
        _items.Add(item);

        Recalculate();
        Touch();
        return item;
    }

    /// <summary>
    /// Removes an item from the sale (used by full-replace updates).
    /// </summary>
    public void RemoveItem(Guid itemId)
    {
        EnsureNotCancelled();
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new DomainException($"Item '{itemId}' is not part of sale '{SaleNumber}'.");
        _items.Remove(item);
        Recalculate();
        Touch();
    }

    /// <summary>
    /// Updates an existing item's quantity, unit price and product name in
    /// place — preserves the item id so external integrations referencing
    /// it stay valid across updates. The item is matched by id; the product
    /// id is verified to prevent silently swapping the line's product.
    /// </summary>
    public SaleItem UpdateItem(Guid itemId, Guid productId, string productName, int quantity, decimal unitPrice)
    {
        EnsureNotCancelled();
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new DomainException($"Item '{itemId}' is not part of sale '{SaleNumber}'.");

        if (item.IsCancelled)
            throw new DomainException($"Item '{itemId}' is cancelled and cannot be updated.");

        if (item.ProductId != productId)
            throw new DomainException(
                $"Item '{itemId}' is for product '{item.ProductId}', cannot be retargeted to '{productId}'.");

        item.Replace(productName, quantity, unitPrice);
        Recalculate();
        Touch();
        return item;
    }

    /// <summary>
    /// Replaces header fields. Sale number cannot change after creation.
    /// </summary>
    public void UpdateHeader(
        DateTime saleDate,
        Guid customerId,
        string customerName,
        Guid branchId,
        string branchName)
    {
        EnsureNotCancelled();
        ValidateHeader(SaleNumber, customerId, customerName, branchId, branchName);

        SaleDate = saleDate;
        CustomerId = customerId;
        CustomerName = customerName;
        BranchId = branchId;
        BranchName = branchName;
        Touch();
    }

    /// <summary>
    /// Soft-cancels the entire sale. Idempotent: cancelling an already
    /// cancelled sale is a no-op and does not raise a duplicate event.
    /// </summary>
    public void Cancel()
    {
        if (IsCancelled)
            return;

        IsCancelled = true;
        foreach (var item in _items.Where(i => !i.IsCancelled))
            item.Cancel();
        Recalculate();
        Touch();

        AddDomainEvent(new SaleCancelledEvent(Id, SaleNumber));
    }

    /// <summary>
    /// Cancels a single item and recalculates the total. Idempotent on the
    /// item: cancelling an already cancelled item is a no-op without event.
    /// </summary>
    public void CancelItem(Guid itemId)
    {
        EnsureNotCancelled();
        var item = _items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new DomainException($"Item '{itemId}' is not part of sale '{SaleNumber}'.");

        if (item.IsCancelled)
            return;

        item.Cancel();
        Recalculate();
        Touch();

        AddDomainEvent(new ItemCancelledEvent(Id, item.Id, item.ProductId, item.Quantity));
    }

    /// <summary>
    /// Kept ONLY for the tests that still construct the aggregate via the
    /// items-less Create + AddItem path. New code paths use the items-aware
    /// <c>Sale.Create(..., items)</c> overload which raises
    /// SaleCreatedEvent automatically. Marked obsolete to nudge callers
    /// toward the encapsulated factory.
    /// </summary>
    [Obsolete("Use the items-aware Sale.Create overload; this method exists only for two-step legacy construction in tests.")]
    public void MarkCreated()
    {
        AddDomainEvent(new SaleCreatedEvent(
            Id, SaleNumber, CustomerId, BranchId, TotalAmount, _items.Count));
    }

    /// <summary>
    /// Raises a SaleModifiedEvent. Called by application handlers after any
    /// post-creation mutation flow completes.
    /// </summary>
    public void MarkModified()
    {
        AddDomainEvent(new SaleModifiedEvent(
            Id, SaleNumber, TotalAmount, _items.Count));
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    private void Recalculate()
    {
        var activeItems = _items.Where(i => !i.IsCancelled).ToArray();
        // LINQ.Sum has no Money overload — fold via the VO's + operator so
        // every intermediate sum stays normalized at 2 dp instead of
        // accumulating raw decimals and rounding only at the end.
        TotalAmount = activeItems.Aggregate(Money.Zero, (acc, i) => acc + i.TotalAmount);
        ActiveItemsCount = activeItems.Length;
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;

    private void EnsureNotCancelled()
    {
        if (IsCancelled)
            throw new DomainException($"Sale '{SaleNumber}' is cancelled and cannot be modified.");
    }

    private static void ValidateHeader(
        string saleNumber, Guid customerId, string customerName, Guid branchId, string branchName)
    {
        if (string.IsNullOrWhiteSpace(saleNumber))
            throw new DomainException("Sale number is required.");
        if (customerId == Guid.Empty)
            throw new DomainException("Customer id is required.");
        if (string.IsNullOrWhiteSpace(customerName))
            throw new DomainException("Customer name is required.");
        if (branchId == Guid.Empty)
            throw new DomainException("Branch id is required.");
        if (string.IsNullOrWhiteSpace(branchName))
            throw new DomainException("Branch name is required.");
    }
}
