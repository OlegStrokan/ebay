using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Application.Mappers;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.CapturePayment;

internal sealed class CapturePaymentCommandHandler(
    IPaymentRepository paymentRepository,
    IStripePaymentProvider stripePaymentProvider,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<CapturePaymentCommandHandler> logger)
    : IRequestHandler<CapturePaymentCommand, Result<ProcessPaymentResultDto>>
{
    public async Task<Result<ProcessPaymentResultDto>> Handle(
        CapturePaymentCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = IdempotencyKey.From(request.IdempotencyKey);
            var existing = await paymentRepository.GetByOrderIdAndIdempotencyKeyAsync(
                request.OrderId,
                idempotencyKey,
                cancellationToken);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Returning idempotent capture response for order {OrderId} and key {IdempotencyKey}",
                    request.OrderId,
                    request.IdempotencyKey);

                return Result<ProcessPaymentResultDto>.Success(
                    PaymentDtoMapper.ToProcessPaymentResult(existing));
            }

            var amount = Money.Create(request.Amount, request.Currency);

            // Look up the existing pre-auth payment by ProviderPaymentIntentId instead of creating a duplicate
            var providerIntentIdValue = ProviderPaymentIntentId.From(request.ProviderPaymentIntentId);
            var existingPayment = await paymentRepository.GetByProviderPaymentIntentIdAsync(
                providerIntentIdValue,
                cancellationToken);

            Payment payment;

            if (existingPayment is null)
            {
                // No pre-auth payment found — direct capture without prior authorization.
                payment = Payment.Create(
                    PaymentId.CreateUnique(),
                    request.OrderId,
                    request.CustomerId,
                    amount,
                    PaymentMethod.Card,
                    idempotencyKey,
                    clock.UtcNow);
            }
            else
            {
                payment = existingPayment;
            }

            var providerResult = await stripePaymentProvider.CapturePaymentAsync(
                new CapturePaymentProviderRequest(
                    PaymentId: payment.Id.Value,
                    OrderId: payment.OrderId,
                    CustomerId: payment.CustomerId,
                    ProviderPaymentIntentId: request.ProviderPaymentIntentId,
                    Amount: payment.Amount.Amount,
                    Currency: payment.Amount.Currency,
                    IdempotencyKey: idempotencyKey.Value),
                cancellationToken);

            var now = clock.UtcNow;
            ProcessPaymentStatus responseStatus;

            if (providerResult.Status == ProviderProcessPaymentStatus.Succeeded)
            {
                var providerIntentId = string.IsNullOrWhiteSpace(providerResult.ProviderPaymentIntentId)
                    ? null
                    : ProviderPaymentIntentId.From(providerResult.ProviderPaymentIntentId);

                payment.MarkSucceeded(providerIntentId, now);
                responseStatus = ProcessPaymentStatus.Succeeded;
            }
            else
            {
                var reason = FailureReason.Create(
                    providerResult.ErrorCode,
                    providerResult.ErrorMessage ?? "Capture returned non-succeeded status.");

                payment.MarkFailed(reason, now);
                responseStatus = ProcessPaymentStatus.Failed;
            }

            if (existingPayment is null)
            {
                await paymentRepository.AddAsync(payment, cancellationToken);
            }
            else
            {
                await paymentRepository.UpdateAsync(payment, cancellationToken);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException ex)
            {
                logger.LogInformation(
                    ex,
                    "Detected concurrent idempotent capture for order {OrderId} and key {IdempotencyKey}. Returning persisted payment.",
                    request.OrderId,
                    request.IdempotencyKey);

                var persistedPayment = await paymentRepository.GetByOrderIdAndIdempotencyKeyAsync(
                    request.OrderId,
                    idempotencyKey,
                    cancellationToken);

                if (persistedPayment is not null)
                {
                    return Result<ProcessPaymentResultDto>.Success(
                        PaymentDtoMapper.ToProcessPaymentResult(persistedPayment));
                }

                logger.LogWarning(
                    ex,
                    "Unique constraint violation for order {OrderId}, key {IdempotencyKey}, but no persisted payment found after conflict.",
                    request.OrderId,
                    request.IdempotencyKey);

                throw;
            }

            logger.LogInformation(
                "Capture completed. OrderId={OrderId}, PaymentId={PaymentId}, Status={Status}",
                request.OrderId,
                payment.Id.Value,
                responseStatus);

            return Result<ProcessPaymentResultDto>.Success(
                PaymentDtoMapper.ToProcessPaymentResult(
                    payment,
                    overrideStatus: responseStatus,
                    errorCode: providerResult.ErrorCode,
                    errorMessage: providerResult.ErrorMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capture failed for order {OrderId}", request.OrderId);
            return Result<ProcessPaymentResultDto>.Failure($"Capture failed: {ex.Message}");
        }
    }
}
