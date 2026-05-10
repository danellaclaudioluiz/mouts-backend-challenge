using Ambev.DeveloperEvaluation.Domain.Common;
using Ambev.DeveloperEvaluation.Domain.Events;
using Ambev.DeveloperEvaluation.Domain.Exceptions;

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
    public decimal TotalAmount { get; private set; }
    public bool IsCancelled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token (Postgres <c>xmin</c>). Increments on every
    /// row update; clients can derive an HTTP ETag from it for If-Match
    /// preconditions on PUT/DELETE.
    /// </summary>
    public uint RowVersion { get; private set; }

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
    /// Creates a new sale with header data only. Items must be added afterwards
    /// via <see cref="AddItem"/>; <see cref="SaleCreatedEvent"/> is raised
    /// once construction completes.
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
            TotalAmount = 0m
        };

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
    /// Raises a SaleCreatedEvent. Called by application handlers after the
    /// initial AddItem loop completes so the event payload reflects final
    /// totals.
    /// </summary>
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
        TotalAmount = _items
            .Where(i => !i.IsCancelled)
            .Sum(i => i.TotalAmount);
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
