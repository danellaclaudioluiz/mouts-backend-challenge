using Ambev.DeveloperEvaluation.Application.Sales.DeleteSale;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class DeleteSaleHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly DeleteSaleHandler _handler;

    public DeleteSaleHandlerTests()
    {
        _handler = new DeleteSaleHandler(_saleRepository);
    }

    [Fact(DisplayName = "Existing sale is removed")]
    public async Task Handle_ExistingSale_DeletesAndReturnsUnit()
    {
        var id = Guid.NewGuid();
        _saleRepository.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        await _handler.Handle(new DeleteSaleCommand(id), CancellationToken.None);

        await _saleRepository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Unknown sale id throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _saleRepository.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var act = () => _handler.Handle(new DeleteSaleCommand(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
    }
}
