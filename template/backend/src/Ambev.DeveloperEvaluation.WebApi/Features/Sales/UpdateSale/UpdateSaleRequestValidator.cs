using Ambev.DeveloperEvaluation.WebApi.Features.Sales.CreateSale;
using FluentValidation;

namespace Ambev.DeveloperEvaluation.WebApi.Features.Sales.UpdateSale;

public class UpdateSaleRequestValidator : AbstractValidator<UpdateSaleRequest>
{
    public UpdateSaleRequestValidator()
    {
        RuleFor(r => r.SaleDate).NotEmpty();
        RuleFor(r => r.CustomerId).NotEmpty();
        RuleFor(r => r.CustomerName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.BranchName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Items).NotEmpty();
        RuleForEach(r => r.Items).SetValidator(new CreateSaleItemRequestValidator());
    }
}
