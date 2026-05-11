namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Full sale projection returned to clients — header fields plus all items.
/// </summary>
public class SaleDto
{
    public Guid Id { get; set; }
    public string SaleNumber { get; set; } = string.Empty;
    public DateTime SaleDate { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public long RowVersion { get; set; }
    /// <summary>
    /// Count of non-cancelled line items, denormalised from the aggregate
    /// (matches Sale.ActiveItemsCount). Lets clients render the headline
    /// "X items active" count without re-walking the items array.
    /// </summary>
    public int ActiveItemsCount { get; set; }
    public List<SaleItemDto> Items { get; set; } = new();
}
