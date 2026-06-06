using FluentValidation;

namespace Application.Commands.ProcessPayment;

public sealed class ProcessPaymentCommandValidator : AbstractValidator<ProcessPaymentCommand>
{
    public ProcessPaymentCommandValidator()
    {
        RuleFor(x => x.OrderId)
            .NotEmpty().WithMessage("OrderId is required.")
            .MaximumLength(128).WithMessage("OrderId must not exceed 128 characters.");

        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("CustomerId is required.")
            .MaximumLength(128).WithMessage("CustomerId must not exceed 128 characters.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.");

        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Currency is required.")
            .Length(3).WithMessage("Currency must be a 3-letter ISO code.");

        RuleFor(x => x.IdempotencyKey)
            .NotEmpty().WithMessage("IdempotencyKey is required.")
            .MaximumLength(128).WithMessage("IdempotencyKey must not exceed 128 characters.");

        RuleFor(x => x.CustomerEmail)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.CustomerEmail))
            .WithMessage("CustomerEmail must be a valid email address.");
    }
}