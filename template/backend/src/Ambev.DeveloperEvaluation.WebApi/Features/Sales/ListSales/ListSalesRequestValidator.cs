using FluentValidation;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales.ListSales;

public class ListSalesRequestValidator : AbstractValidator<ListSalesRequest>
{
    public ListSalesRequestValidator()
    {
        RuleFor(r => r.Page).GreaterThanOrEqualTo(1);
        RuleFor(r => r.Size).InclusiveBetween(1, 100);
    }
}
