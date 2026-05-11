namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Item-level projection used by every read result that returns a sale
/// (Get, List, Create, Update, Cancel). Centralised so the contract stays
/// consistent across endpoints.
/// </summary>
public class SaleItemDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal TotalAmount { get; set; }
    public bool IsCancelled { get; set; }
}
