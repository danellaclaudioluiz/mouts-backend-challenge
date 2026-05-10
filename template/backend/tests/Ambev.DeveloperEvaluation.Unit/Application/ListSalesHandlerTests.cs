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

    private static SaleSummary BuildSummary() => new(
        Guid.NewGuid(), "S-1", DateTime.UtcNow, Guid.NewGuid(), "C", Guid.NewGuid(), "B",
        10m, false, DateTime.UtcNow, null, 1);

    [Fact(DisplayName = "Page mode translates query into filter and returns counts")]
    public async Task Handle_PageMode_DelegatesToRepository()
    {
        var summary = BuildSummary();
        _saleRepository.ListAsync(Arg.Any<SaleListFilter>(), Arg.Any<CancellationToken>())
            .Returns(new SalePage(new[] { summary }, TotalCount: 1L, NextCursor: null));

        var result = await _handler.Handle(
            new ListSalesQuery { Page = 2, Size = 25, Order = "saleDate desc", IsCancelled = false },
            CancellationToken.None);

        result.Page.Should().Be(2);
        result.Size.Should().Be(25);
        result.TotalCount.Should().Be(1);
        result.TotalPages.Should().Be(1);
        result.NextCursor.Should().BeNull();
        result.Items.Should().HaveCount(1);

        await _saleRepository.Received(1).ListAsync(
            Arg.Is<SaleListFilter>(f =>
                f.Page == 2 &&
                f.Size == 25 &&
                f.Order == "saleDate desc" &&
                f.IsCancelled == false &&
                f.Cursor == null),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Cursor mode forwards the cursor and returns NextCursor without counts")]
    public async Task Handle_CursorMode_PassesThrough()
    {
        var summary = BuildSummary();
        _saleRepository.ListAsync(Arg.Any<SaleListFilter>(), Arg.Any<CancellationToken>())
            .Returns(new SalePage(new[] { summary }, TotalCount: null, NextCursor: "next"));

        var result = await _handler.Handle(
            new ListSalesQuery { Size = 25, Cursor = "abc" },
            CancellationToken.None);

        result.TotalCount.Should().BeNull();
        result.TotalPages.Should().BeNull();
        result.NextCursor.Should().Be("next");

        await _saleRepository.Received(1).ListAsync(
            Arg.Is<SaleListFilter>(f => f.Cursor == "abc"),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Empty repository result yields empty page")]
    public async Task Handle_EmptyResult_ReturnsZeroCount()
    {
        _saleRepository.ListAsync(Arg.Any<SaleListFilter>(), Arg.Any<CancellationToken>())
            .Returns(new SalePage(Array.Empty<SaleSummary>(), TotalCount: 0L, NextCursor: null));

        var result = await _handler.Handle(new ListSalesQuery(), CancellationToken.None);

        result.TotalCount.Should().Be(0);
        result.TotalPages.Should().Be(0);
        result.Items.Should().BeEmpty();
    }
}
