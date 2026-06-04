using FluentValidation;

namespace Application.Commands.CreateProduct;

public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.SellerId)
            .NotEmpty().WithMessage("Seller ID is required.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Product name is required.")
            .MaximumLength(200).WithMessage("Product name must not exceed 200 characters.");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-character ISO code.");

        RuleFor(x => x.InitialStock)
            .GreaterThanOrEqualTo(0).WithMessage("Initial stock cannot be negative.");
    }
}
