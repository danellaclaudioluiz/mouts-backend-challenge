namespace Ambev.DeveloperEvaluation.Integration.Helpers;

/// <summary>
/// Reusable payload builders for the Sales endpoints. Keeping every test
/// from re-spelling the same anonymous-object shape, and giving overrides
/// for the few fields a particular test wants to tweak.
/// </summary>
public static class PayloadBuilder
{
    public static object BuildCreate(
        string saleNumber,
        int quantity = 5,
        decimal unitPrice = 10m,
        DateTime? saleDate = null,
        Guid? customerId = null,
        Guid? branchId = null,
        IEnumerable<(Guid ProductId, string Name, int Qty, decimal Price)>? extraItems = null)
    {
        var items = new List<object>
        {
            new
            {
                ProductId = Guid.NewGuid(),
                ProductName = "Beer",
                Quantity = quantity,
                UnitPrice = unitPrice
            }
        };
        if (extraItems is not null)
        {
            foreach (var it in extraItems)
                items.Add(new
                {
                    ProductId = it.ProductId,
                    ProductName = it.Name,
                    Quantity = it.Qty,
                    UnitPrice = it.Price
                });
        }

        return new
        {
            SaleNumber = saleNumber,
            SaleDate = saleDate ?? DateTime.UtcNow,
            CustomerId = customerId ?? Guid.NewGuid(),
            CustomerName = "Acme",
            BranchId = branchId ?? Guid.NewGuid(),
            BranchName = "Branch 1",
            Items = items
        };
    }

    /// <summary>
    /// Builds a PUT body that keeps the same single product as the create
    /// payload (by id) so the diff path exercises UpdateItem instead of
    /// Add + Remove.
    /// </summary>
    public static object BuildUpdate(
        Guid productId,
        string productName = "Beer",
        int quantity = 5,
        decimal unitPrice = 10m,
        string customerName = "Updated",
        DateTime? saleDate = null) =>
        new
        {
            SaleDate = saleDate ?? DateTime.UtcNow,
            CustomerId = Guid.NewGuid(),
            CustomerName = customerName,
            BranchId = Guid.NewGuid(),
            BranchName = "B",
            Items = new[]
            {
                new
                {
                    ProductId = productId,
                    ProductName = productName,
                    Quantity = quantity,
                    UnitPrice = unitPrice
                }
            }
        };
}

/// <summary>
/// Wire shape for ApiResponseWithData&lt;SaleDto&gt; — used by every
/// test that needs to read the created/updated sale's id, total or row
/// version off the response.
/// </summary>
public sealed record EnvelopedSale(SalePayload Data);

public sealed record SalePayload(
    Guid Id,
    string SaleNumber,
    decimal TotalAmount,
    bool IsCancelled,
    long RowVersion,
    int ActiveItemsCount,
    IReadOnlyList<SaleItemPayload> Items);

public sealed record SaleItemPayload(Guid Id, Guid ProductId, int Quantity, decimal UnitPrice, decimal TotalAmount, bool IsCancelled);

public sealed record EnvelopedList(
    IReadOnlyList<SalePayload> Data,
    int CurrentPage,
    long? TotalPages,
    long? TotalCount,
    string? NextCursor);
