using FluentValidation;

namespace Application.Commands.CancelAuthorization;

public sealed class CancelAuthorizationCommandValidator : AbstractValidator<CancelAuthorizationCommand>
{
    public CancelAuthorizationCommandValidator()
    {
        RuleFor(x => x.ProviderPaymentIntentId)
            .NotEmpty().WithMessage("ProviderPaymentIntentId is required.")
            .MaximumLength(256).WithMessage("ProviderPaymentIntentId must not exceed 256 characters.");
    }
}