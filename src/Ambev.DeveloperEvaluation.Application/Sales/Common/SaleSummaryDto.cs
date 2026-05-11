namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Header-only sale projection returned by list endpoints. Item details
/// are not included to keep the list query cheap; clients that need items
/// should call GET /api/sales/{id}.
/// </summary>
public class SaleSummaryDto
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
    public int ItemCount { get; set; }
}
