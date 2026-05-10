using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.GetSale;
using Ambev.DeveloperEvaluation.Domain.Entities;
using Ambev.DeveloperEvaluation.Domain.Exceptions;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using Ambev.DeveloperEvaluation.Unit.Domain.Entities.TestData;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class GetSaleHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly GetSaleHandler _handler;

    public GetSaleHandlerTests()
    {
        _handler = new GetSaleHandler(_saleRepository, _mapper);
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    [Fact(DisplayName = "Returns the sale when it exists")]
    public async Task Handle_KnownSale_ReturnsDto()
    {
        var sale = SaleTestData.GenerateValidSale();
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        var result = await _handler.Handle(new GetSaleQuery(sale.Id), CancellationToken.None);

        result.Id.Should().Be(sale.Id);
    }

    [Fact(DisplayName = "Unknown id throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _saleRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var act = () => _handler.Handle(new GetSaleQuery(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
    }
}
