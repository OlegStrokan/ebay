using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(
    IPaymentWebhookEventRepository paymentWebhookEventRepository,
    IPaymentRepository paymentRepository,
    IRefundRepository refundRepository,
    IOrderCallbackQueueService orderCallbackQueueService,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<HandleStripeWebhookCommandHandler> logger)
    : IRequestHandler<HandleStripeWebhookCommand, Result<WebhookProcessingResultDto>>
{
    public async Task<Result<WebhookProcessingResultDto>> Handle(
        HandleStripeWebhookCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await paymentWebhookEventRepository.GetByProviderEventIdAsync(
                request.ProviderEventId,
                cancellationToken);

            if (existing is not null)
            {
                return Result<WebhookProcessingResultDto>.Success(new WebhookProcessingResultDto(
                    IsDuplicate: true,
                    Processed: false,
                    IsIgnored: true,
                    ProviderEventId: request.ProviderEventId,
                    EventType: request.EventType,
                    PaymentId: request.PaymentId,
                    Error: null));
            }

            var now = clock.UtcNow;
            var webhookEvent = PaymentWebhookEvent.Create(
                request.ProviderEventId,
                request.EventType,
                request.PayloadJson,
                now);

            await paymentWebhookEventRepository.AddAsync(webhookEvent, cancellationToken);

            if (request.Outcome == StripeWebhookOutcome.Unknown)
            {
                webhookEvent.MarkProcessed(now);
                await paymentWebhookEventRepository.UpdateAsync(webhookEvent, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                return Result<WebhookProcessingResultDto>.Success(new WebhookProcessingResultDto(
                    IsDuplicate: false,
                    Processed: true,
                    IsIgnored: true,
                    ProviderEventId: request.ProviderEventId,
                    EventType: request.EventType,
                    PaymentId: request.PaymentId,
                    Error: null));
            }

            var payment = await ResolvePaymentAsync(request, cancellationToken);

            if (payment is null)
            {
                var error =
                    $"Payment could not be resolved for webhook event '{request.ProviderEventId}' " +
                    $"PaymentId='{request.PaymentId}', ProviderPaymentIntentId='{request.ProviderPaymentIntentId}', ProviderRefundId='{request.ProviderRefundId}'";
                webhookEvent.MarkFailed(error, now);
                await paymentWebhookEventRepository.UpdateAsync(webhookEvent, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                return Result<WebhookProcessingResultDto>.Failure(error);
            }

            await ApplyOutcomeAsync(request, payment, now, cancellationToken);

            await paymentRepository.UpdateAsync(payment, cancellationToken);
            webhookEvent.MarkProcessed(now);
            await paymentWebhookEventRepository.UpdateAsync(webhookEvent, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result<WebhookProcessingResultDto>.Success(new WebhookProcessingResultDto(
                IsDuplicate: false,
                Processed: true,
                IsIgnored: false,
                ProviderEventId: request.ProviderEventId,
                EventType: request.EventType,
                PaymentId: payment.Id.Value,
                Error: null));
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "HandleStripeWebhook domain validation failed");
            return Result<WebhookProcessingResultDto>.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HandleStripeWebhook failed unexpectedly");
            return Result<WebhookProcessingResultDto>.Failure("Unexpected error while processing webhook event.");
        }
    }

    private async Task ApplyOutcomeAsync(
        HandleStripeWebhookCommand request,
        Payment payment,
        DateTime now,
        CancellationToken cancellationToken)
    {
        switch (request.Outcome)
        {
            case StripeWebhookOutcome.PaymentSucceeded:
            {
                if (!IsApplicable(payment.Status))
                {
                    return;
                }

                var providerPaymentIntentId = string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId)
                    ? payment.ProviderPaymentIntentId
                    : ProviderPaymentIntentId.From(request.ProviderPaymentIntentId);

                if (providerPaymentIntentId is null)
                {
                    throw new InvalidValueException(
                        "ProviderPaymentIntentId is required for PaymentSucceeded webhook outcomes.");
                }

                payment.MarkSucceeded(providerPaymentIntentId, now);
                await orderCallbackQueueService.QueuePaymentSucceededAsync(payment, cancellationToken);
                return;
            }
            case StripeWebhookOutcome.PaymentFailed:
            {
                if (!IsApplicable(payment.Status))
                {
                    return;
                }

                var reason = FailureReason.Create(
                    request.FailureCode,
                    request.FailureMessage ?? "Payment failed via webhook callback.");

                payment.MarkFailed(reason, now);
                await orderCallbackQueueService.QueuePaymentFailedAsync(payment, reason, cancellationToken);
                return;
            }
            case StripeWebhookOutcome.RefundSucceeded:
            {
                var refund = await refundRepository.GetPendingByPaymentIdAsync(payment.Id, cancellationToken);
                if (refund is null)
                {
                    return;
                }

                var providerRefundId = string.IsNullOrWhiteSpace(request.ProviderRefundId)
                    ? refund.ProviderRefundId
                    : ProviderRefundId.From(request.ProviderRefundId);

                if (providerRefundId is null)
                {
                    throw new InvalidValueException(
                        "ProviderRefundId is required for RefundSucceeded webhook outcomes.");
                }

                refund.MarkSucceeded(providerRefundId, now);
                await refundRepository.UpdateAsync(refund, cancellationToken);

                if (payment.Status is PaymentStatus.RefundPending or PaymentStatus.RefundFailed)
                {
                    payment.MarkRefunded(refund.Id, providerRefundId, refund.Amount, now);
                }

                await orderCallbackQueueService.QueueRefundSucceededAsync(payment, refund, cancellationToken);
                return;
            }
            case StripeWebhookOutcome.RefundFailed:
            {
                var refund = await refundRepository.GetPendingByPaymentIdAsync(payment.Id, cancellationToken);
                if (refund is null)
                {
                    return;
                }

                var reason = FailureReason.Create(
                    request.FailureCode,
                    request.FailureMessage ?? "Refund failed via webhook callback.");

                refund.MarkFailed(reason, now);
                await refundRepository.UpdateAsync(refund, cancellationToken);

                if (payment.Status == PaymentStatus.RefundPending)
                {
                    payment.MarkRefundFailed(refund.Id, reason, now);
                }

                await orderCallbackQueueService.QueueRefundFailedAsync(payment, refund, reason, cancellationToken);
                return;
            }
            default:
                return;
        }
    }

    private static bool IsApplicable(PaymentStatus status) =>
        status is PaymentStatus.Created
            or PaymentStatus.PendingProviderConfirmation
            or PaymentStatus.Authorized;

    private async Task<Payment?> ResolvePaymentAsync(
        HandleStripeWebhookCommand request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.PaymentId))
        {
            return await paymentRepository.GetByIdAsync(PaymentId.From(request.PaymentId), cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return await paymentRepository.GetByProviderPaymentIntentIdAsync(
                ProviderPaymentIntentId.From(request.ProviderPaymentIntentId),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ProviderRefundId))
        {
            var refund = await refundRepository.GetByProviderRefundIdAsync(
                ProviderRefundId.From(request.ProviderRefundId),
                cancellationToken);

            if (refund is null)
            {
                return null;
            }

            return await paymentRepository.GetByIdAsync(refund.PaymentId, cancellationToken);
        }

        return null;
    }
}