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
    private readonly ISaleReadCache _cache = Substitute.For<ISaleReadCache>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly GetSaleHandler _handler;

    public GetSaleHandlerTests()
    {
        _handler = new GetSaleHandler(_saleRepository, _cache, _mapper);
        _mapper.Map<SaleDto>(Arg.Any<Sale>())
            .Returns(callInfo => new SaleDto { Id = callInfo.Arg<Sale>().Id });
    }

    [Fact(DisplayName = "Cache hit short-circuits the repository")]
    public async Task Handle_CacheHit_SkipsRepository()
    {
        var id = Guid.NewGuid();
        _cache.TryGetAsync(id, Arg.Any<CancellationToken>())
            .Returns(new SaleDto { Id = id });

        var result = await _handler.Handle(new GetSaleQuery(id), CancellationToken.None);

        result.Id.Should().Be(id);
        await _saleRepository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Cache miss falls through to repository and repopulates")]
    public async Task Handle_CacheMiss_FetchesAndCaches()
    {
        var sale = SaleTestData.GenerateValidSale();
        _cache.TryGetAsync(sale.Id, Arg.Any<CancellationToken>())
            .Returns((SaleDto?)null);
        _saleRepository.GetByIdAsync(sale.Id, Arg.Any<CancellationToken>()).Returns(sale);

        var result = await _handler.Handle(new GetSaleQuery(sale.Id), CancellationToken.None);

        result.Id.Should().Be(sale.Id);
        await _cache.Received(1).SetAsync(
            Arg.Is<SaleDto>(d => d.Id == sale.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Unknown id throws ResourceNotFoundException")]
    public async Task Handle_UnknownSale_Throws()
    {
        _cache.TryGetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((SaleDto?)null);
        _saleRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var act = () => _handler.Handle(new GetSaleQuery(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<ResourceNotFoundException>();
    }
}
