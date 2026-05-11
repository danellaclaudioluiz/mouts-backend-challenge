using MediatR;

namespace Ambev.DeveloperEvaluation.Application.Sales.DeleteSale;

public class DeleteSaleCommand : IRequest<Unit>
{
    public Guid Id { get; set; }

    /// <summary>Optional If-Match precondition (see UpdateSaleCommand).</summary>
    public long? ExpectedRowVersion { get; set; }

    public DeleteSaleCommand() { }
    public DeleteSaleCommand(Guid id) => Id = id;
    public DeleteSaleCommand(Guid id, long? expectedRowVersion)
    {
        Id = id;
        ExpectedRowVersion = expectedRowVersion;
    }
}
