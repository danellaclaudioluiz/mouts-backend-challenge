using Ambev.DeveloperEvaluation.Application.Sales.Common;
using Ambev.DeveloperEvaluation.Application.Sales.ListSales;
using Ambev.DeveloperEvaluation.Domain.Repositories;
using AutoMapper;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ambev.DeveloperEvaluation.Unit.Application;

public class ListSalesHandlerTests
{
    private readonly ISaleRepository _saleRepository = Substitute.For<ISaleRepository>();
    private readonly IMapper _mapper = Substitute.For<IMapper>();
    private readonly ListSalesHandler _handler;

    public ListSalesHandlerTests()
    {
        _handler = new ListSalesHandler(_saleRepository, _mapper);
        _mapper.Map<SaleSummaryDto>(Arg.Any<SaleSummary>())
            .Returns(callInfo => new SaleSummaryDto { Id = callInfo.Arg<SaleSummary>().Id });
    }

    [Fact(DisplayName = "Translates query into filter and returns paged result")]
    public async Task Handle_ValidQuery_DelegatesToRepository()
    {
        var summary = new SaleSummary(
            Guid.NewGuid(), "S-1", DateTime.UtcNow, Guid.NewGuid(), "C", Guid.NewGuid(), "B",
            10m, false, DateTime.UtcNow, null, 1);
        _saleRepository.ListAsync(Arg.Any<SaleListFilter>(), Arg.Any<CancellationToken>())
            .Returns((new[] { summary }, 1L));

        var result = await _handler.Handle(
            new ListSalesQuery { Page = 2, Size = 25, Order = "saleDate desc", IsCancelled = false },
            CancellationToken.None);

        result.Page.Should().Be(2);
        result.Size.Should().Be(25);
        result.TotalCount.Should().Be(1);
        result.TotalPages.Should().Be(1);
        result.Items.Should().HaveCount(1);

        await _saleRepository.Received(1).ListAsync(
            Arg.Is<SaleListFilter>(f =>
                f.Page == 2 &&
                f.Size == 25 &&
                f.Order == "saleDate desc" &&
                f.IsCancelled == false),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Empty repository result yields empty page")]
    public async Task Handle_EmptyResult_ReturnsZeroCount()
    {
        _saleRepository.ListAsync(Arg.Any<SaleListFilter>(), Arg.Any<CancellationToken>())
            .Returns((Array.Empty<SaleSummary>(), 0L));

        var result = await _handler.Handle(new ListSalesQuery(), CancellationToken.None);

        result.TotalCount.Should().Be(0);
        result.TotalPages.Should().Be(0);
        result.Items.Should().BeEmpty();
    }
}
