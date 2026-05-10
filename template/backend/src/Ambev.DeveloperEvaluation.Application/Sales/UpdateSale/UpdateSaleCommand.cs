using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.UpdateSale;

/// <summary>
/// Full-replace update of a sale. Header fields and item set are both
/// replaced wholesale. SaleNumber cannot be changed after creation, so
/// it is not part of the command.
/// </summary>
public class UpdateSaleCommand : IRequest<SaleDto>
{
    public Guid Id { get; set; }
    public DateTime SaleDate { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public Guid BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public List<CreateSaleItemDto> Items { get; set; } = new();

    /// <summary>
    /// If supplied (typically derived from an If-Match HTTP header), the
    /// handler aborts with 412 Precondition Failed when the persisted sale's
    /// RowVersion is different — explicit optimistic concurrency control at
    /// the request boundary, before any state mutates.
    /// </summary>
    public long? ExpectedRowVersion { get; set; }
}
