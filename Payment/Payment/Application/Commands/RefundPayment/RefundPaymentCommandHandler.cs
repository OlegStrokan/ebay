using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Application.Mappers;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.RefundPayment;

internal sealed class RefundPaymentCommandHandler(
    IPaymentRepository paymentRepository,
    IRefundRepository refundRepository,
    IStripePaymentProvider stripePaymentProvider,
    IMoneyEventQueueService moneyEventQueueService,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RefundPaymentCommandHandler> logger)
    : IRequestHandler<RefundPaymentCommand, Result<RefundPaymentResultDto>>
{
    public async Task<Result<RefundPaymentResultDto>> Handle(
        RefundPaymentCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var paymentId = PaymentId.From(request.PaymentId);
            var payment = await paymentRepository.GetByIdAsync(paymentId, cancellationToken);

            if (payment is null)
            {
                return Result<RefundPaymentResultDto>.Failure($"Payment '{request.PaymentId}' was not found.");
            }

            var idempotencyKey = IdempotencyKey.From(request.IdempotencyKey);
            var existingRefund = await refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                paymentId,
                idempotencyKey,
                cancellationToken);

            if (existingRefund is not null)
            {
                logger.LogInformation(
                    "Returning idempotent refund response for payment {PaymentId} and key {IdempotencyKey}",
                    request.PaymentId,
                    request.IdempotencyKey);

                return Result<RefundPaymentResultDto>.Success(
                    PaymentDtoMapper.ToRefundPaymentResult(payment, existingRefund));
            }

            var amount = Money.Create(request.Amount, request.Currency);
            var now = clock.UtcNow;

            var refund = Refund.Create(
                paymentId,
                amount,
                request.Reason,
                idempotencyKey,
                now);

            payment.StartRefund(refund.Id, amount, request.Reason, now);

            var providerResult = await stripePaymentProvider.RefundPaymentAsync(
                new RefundPaymentProviderRequest(
                    PaymentId: payment.Id.Value,
                    ProviderPaymentIntentId: payment.ProviderPaymentIntentId?.Value,
                    Amount: amount.Amount,
                    Currency: amount.Currency,
                    Reason: request.Reason,
                    IdempotencyKey: idempotencyKey.Value),
                cancellationToken);

            RefundPaymentStatus responseStatus;

            switch (providerResult.Status)
            {
                case ProviderRefundPaymentStatus.Succeeded:
                {
                    if (string.IsNullOrWhiteSpace(providerResult.ProviderRefundId))
                    {
                        return Result<RefundPaymentResultDto>.Failure(
                            "ProviderRefundId is required when provider refund status is Succeeded.");
                    }

                    var providerRefundId = ProviderRefundId.From(providerResult.ProviderRefundId);
                    refund.MarkSucceeded(providerRefundId, now);
                    payment.MarkRefunded(refund.Id, refund.Amount, providerRefundId, now);
                    responseStatus = RefundPaymentStatus.Succeeded;
                    await moneyEventQueueService.QueueRefundIssuedAsync(payment, refund, cancellationToken);
                    break;
                }
                case ProviderRefundPaymentStatus.Pending:
                {
                      if (string.IsNullOrWhiteSpace(providerResult.ProviderRefundId))
                      {
                          return Result<RefundPaymentResultDto>.Failure(
                              "ProviderRefundId is required when provider refund status is Pending");
                      }

                      var providerRefundId = ProviderRefundId.From(providerResult.ProviderRefundId);

                    refund.MarkPendingProviderConfirmation(providerRefundId, now);
                    responseStatus = RefundPaymentStatus.Pending;
                    break;
                }
                case ProviderRefundPaymentStatus.Failed:
                {
                    var reason = FailureReason.Create(
                        providerResult.ErrorCode,
                        providerResult.ErrorMessage ?? "Refund provider returned failure status.");

                    refund.MarkFailed(reason, now);
                    payment.MarkRefundFailed(refund.Id, reason, now);
                    responseStatus = RefundPaymentStatus.Failed;
                    break;
                }
                default:
                    return Result<RefundPaymentResultDto>.Failure("Unsupported provider refund status.");
            }

            await refundRepository.AddAsync(refund, cancellationToken);
            await paymentRepository.UpdateAsync(payment, cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException ex)
            {
                logger.LogInformation(
                    ex,
                    "Detected concurrent idempotent refund create for payment {PaymentId} and key {IdempotencyKey}. Returning persisted refund.",
                    request.PaymentId,
                    request.IdempotencyKey);

                var persistedRefund = await refundRepository.GetByPaymentIdAndIdempotencyKeyAsync(
                    paymentId,
                    idempotencyKey,
                    cancellationToken);

                if (persistedRefund is not null)
                {
                    var persistedPayment = await paymentRepository.GetByIdAsync(paymentId, cancellationToken) ?? payment;
                    return Result<RefundPaymentResultDto>.Success(
                        PaymentDtoMapper.ToRefundPaymentResult(persistedPayment, persistedRefund));
                }

                logger.LogWarning(
                    ex,
                    "Unique constraint violation occurred for payment {PaymentId}, key {IdempotencyKey}, but no persisted idempotent refund was found after conflict.",
                    request.PaymentId,
                    request.IdempotencyKey);

                return Result<RefundPaymentResultDto>.Failure(
                    "Concurrent idempotent refund conflict. Please retry the request.");
            }

            return Result<RefundPaymentResultDto>.Success(
                PaymentDtoMapper.ToRefundPaymentResult(
                    payment,
                    refund,
                    overrideStatus: responseStatus,
                    errorCode: providerResult.ErrorCode,
                    errorMessage: providerResult.ErrorMessage));
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "RefundPayment domain validation failed");
            return Result<RefundPaymentResultDto>.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RefundPayment failed unexpectedly");
            return Result<RefundPaymentResultDto>.Failure("Unexpected error while processing refund.");
        }
    }
}