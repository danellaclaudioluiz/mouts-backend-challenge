using Ambev.DeveloperEvaluation.Domain.Repositories;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.Application.Sales.ListSales;

public class ListSalesValidator : AbstractValidator<ListSalesQuery>
{
    public ListSalesValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.Size).InclusiveBetween(1, 100);
        RuleFor(q => q.Order).Must(BeAValidOrderExpression!)
            .When(q => !string.IsNullOrWhiteSpace(q.Order))
            .WithMessage(
                "Order may only reference these fields: " +
                string.Join(", ", SaleListFilter.SupportedSortFields) +
                ". Each clause is '<field>' or '<field> asc|desc'.");
    }

    private static bool BeAValidOrderExpression(string order)
    {
        foreach (var raw in order.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2) return false;

            if (!SaleListFilter.SupportedSortFields.Any(f =>
                f.Equals(parts[0], StringComparison.OrdinalIgnoreCase)))
                return false;

            if (parts.Length == 2 &&
                !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase) &&
                !parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
