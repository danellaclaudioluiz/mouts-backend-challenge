using Ambev.DeveloperEvaluation.Application.Sales.CreateSale;
using Ambev.DeveloperEvaluation.Domain.Exceptions;

namespace Ambev.DeveloperEvaluation.Application.Sales.Common;

/// <summary>
/// Shared input checks for any flow that takes a list of sale items
/// (Create, Update). Keeps the policy in one place so Create and Update
/// behave the same way for the same input.
/// </summary>
internal static class SaleItemPayloadGuard
{
    public static void EnsureUniqueProductIds(IEnumerable<CreateSaleItemDto> items)
    {
        var duplicate = items
            .GroupBy(i => i.ProductId)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new DomainException(
                $"Product '{duplicate.Key}' appears in the payload more than once. " +
                "Consolidate quantities for the same product into a single line before sending.");
    }
}
