using Application.Common;
using Application.Gateways;
using Application.Interfaces;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.CancelAuthorization;

internal sealed class CancelAuthorizationCommandHandler(
    IPaymentRepository paymentRepository,
    IStripePaymentProvider stripePaymentProvider,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<CancelAuthorizationCommandHandler> logger)
    : IRequestHandler<CancelAuthorizationCommand, Result>
{
    private const string AuthorizationCanceledCode = "authorization_canceled";
    private const string AuthorizationCanceledMessage = "Authorization canceled via CancelAuthorization request.";

    public async Task<Result> Handle(CancelAuthorizationCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var providerPaymentIntentId = ProviderPaymentIntentId.From(request.ProviderPaymentIntentId);
            var payment = await paymentRepository.GetByProviderPaymentIntentIdAsync(
                providerPaymentIntentId,
                cancellationToken);

            if (payment?.Status == PaymentStatus.Failed
                && string.Equals(payment.FailureReason?.Code, AuthorizationCanceledCode, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "CancelAuthorization treated as idempotent success. ProviderPaymentIntentId={ProviderPaymentIntentId}, PaymentId={PaymentId}",
                    providerPaymentIntentId.Value,
                    payment.Id.Value);

                return Result.Success();
            }

            await stripePaymentProvider.CancelAuthorizationAsync(providerPaymentIntentId.Value, cancellationToken);

            if (payment is null)
            {
                logger.LogInformation(
                    "CancelAuthorization completed but no payment record was found. ProviderPaymentIntentId={ProviderPaymentIntentId}",
                    providerPaymentIntentId.Value);

                return Result.Success();
            }

            if (payment.Status is not (PaymentStatus.Created or PaymentStatus.PendingProviderConfirmation))
            {
                logger.LogInformation(
                    "CancelAuthorization completed with no payment state update due to terminal status. ProviderPaymentIntentId={ProviderPaymentIntentId}, PaymentId={PaymentId}, Status={Status}",
                    providerPaymentIntentId.Value,
                    payment.Id.Value,
                    payment.Status);

                return Result.Success();
            }

            var reason = FailureReason.Create(AuthorizationCanceledCode, AuthorizationCanceledMessage);
            payment.MarkFailed(reason, clock.UtcNow);

            await paymentRepository.UpdateAsync(payment, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "CancelAuthorization completed and payment was marked failed. ProviderPaymentIntentId={ProviderPaymentIntentId}, PaymentId={PaymentId}",
                providerPaymentIntentId.Value,
                payment.Id.Value);

            return Result.Success();
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "CancelAuthorization domain validation failed");
            return Result.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CancelAuthorization failed unexpectedly");
            return Result.Failure($"Cancel authorization failed: {ex.Message}");
        }
    }
}