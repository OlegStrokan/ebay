using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Application.Services;
using Domain.Enums;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.ReconcilePendingPayments;

// periodically run by bg worker
internal sealed class ReconcilePendingPaymentsCommandHandler(
    IPaymentRepository paymentRepository,
    IRefundRepository refundRepository,
    IStripePaymentProvider stripePaymentProvider,
    IOrderCallbackQueueService orderCallbackQueueService,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ReconcilePendingPaymentsCommandHandler> logger)
    : IRequestHandler<ReconcilePendingPaymentsCommand, Result<ReconciliationResultDto>>
{
    public async Task<Result<ReconciliationResultDto>> Handle(
        ReconcilePendingPaymentsCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var threshold = clock.UtcNow.AddMinutes(-request.OlderThanMinutes);

            var paymentsChecked = 0;
            var paymentsSucceeded = 0;
            var paymentsFailed = 0;
            var refundsChecked = 0;
            var refundsSucceeded = 0;
            var refundsFailed = 0;
            var callbacksQueued = 0;

            var pendingPayments = await paymentRepository.GetPendingProviderConfirmationsOlderThanAsync(
                threshold,
                request.BatchSize,
                cancellationToken);

            foreach (var payment in pendingPayments)
            {
                paymentsChecked++;

                var providerPaymentIntentId = payment.ProviderPaymentIntentId?.Value;
                if (string.IsNullOrWhiteSpace(providerPaymentIntentId))
                {
                    continue;
                }

                var providerStatus = await stripePaymentProvider.GetPaymentStatusAsync(
                    providerPaymentIntentId,
                    cancellationToken);

                switch (providerStatus.Status)
                {
                    case ProviderPaymentLifecycleStatus.Succeeded:
                    {
                        if (payment.Status is not PaymentStatus.Created and not PaymentStatus.PendingProviderConfirmation)
                        {
                            continue;
                        }

                        payment.MarkSucceeded(payment.ProviderPaymentIntentId, clock.UtcNow);
                        await paymentRepository.UpdateAsync(payment, cancellationToken);
                        await orderCallbackQueueService.QueuePaymentSucceededAsync(payment, cancellationToken);

                        paymentsSucceeded++;
                        callbacksQueued++;
                        break;
                    }

                    case ProviderPaymentLifecycleStatus.Failed:
                    {
                        if (payment.Status is not PaymentStatus.Created and not PaymentStatus.PendingProviderConfirmation)
                        {
                            continue;
                        }

                        var reason = FailureReason.Create(
                            providerStatus.ErrorCode,
                            providerStatus.ErrorMessage
                            ?? "Provider marked payment as failed during reconciliation.");

                        payment.MarkFailed(reason, clock.UtcNow);
                        await paymentRepository.UpdateAsync(payment, cancellationToken);
                        await orderCallbackQueueService.QueuePaymentFailedAsync(payment, reason, cancellationToken);

                        paymentsFailed++;
                        callbacksQueued++;
                        break;
                    }

                    default:
                        continue;
                }

                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to save reconciliation result for payment {PaymentId}; skipping",
                        payment.Id.Value);

                    unitOfWork.DetachUncommittedChanges();
                }
            }

            var pendingRefunds = await refundRepository.GetPendingProviderConfirmationsOlderThanAsync(
                threshold,
                request.BatchSize,
                cancellationToken);

            var refundPaymentIds = pendingRefunds
                .Select(r => r.PaymentId)
                .Distinct()
                .ToList();

            var paymentsById = await paymentRepository.GetByIdsAsync(refundPaymentIds, cancellationToken);

            foreach (var refund in pendingRefunds)
            {
                refundsChecked++;

                if (!paymentsById.TryGetValue(refund.PaymentId, out var payment))
                {
                    logger.LogWarning(
                        "Skipping refund reconciliation for refund {RefundId} because payment {PaymentId} was not found",
                        refund.Id.Value,
                        refund.PaymentId.Value);
                    continue;
                }

                // probability is near 0%: all pending requests must have providerRefundIdValue
                // but shit happens, it is near zero runtime cost so why not
                var providerRefundIdValue = refund.ProviderRefundId?.Value;
                if (string.IsNullOrWhiteSpace(providerRefundIdValue))
                {
                      var reason = FailureReason.Create(
                          "missing_provider_refund_id",
                          "Pending refund is missing ProviderRefundId and cannot be reconciled.");

                      refund.MarkFailed(reason, clock.UtcNow);
                      await refundRepository.UpdateAsync(refund, cancellationToken);

                      if (payment.Status == PaymentStatus.RefundPending)
                      {
                          payment.MarkRefundFailed(refund.Id, reason, clock.UtcNow);
                          await paymentRepository.UpdateAsync(payment, cancellationToken);
                      }

                      await orderCallbackQueueService.QueueRefundFailedAsync(payment, refund, reason, cancellationToken);

                      refundsFailed++;
                      callbacksQueued++;
                    continue;
                }

                var providerStatus = await stripePaymentProvider.GetRefundStatusAsync(
                    providerRefundIdValue,
                    cancellationToken);

                switch (providerStatus.Status)
                {
                    case ProviderRefundLifecycleStatus.Succeeded:
                    {
                        refund.MarkSucceeded(refund.ProviderRefundId!, clock.UtcNow);
                        await refundRepository.UpdateAsync(refund, cancellationToken);

                        if (payment.Status is PaymentStatus.RefundPending or PaymentStatus.RefundFailed)
                        {
                            payment.MarkRefunded(refund.Id, refund.ProviderRefundId, refund.Amount, clock.UtcNow);
                            await paymentRepository.UpdateAsync(payment, cancellationToken);
                        }

                        await orderCallbackQueueService.QueueRefundSucceededAsync(payment, refund, cancellationToken);

                        refundsSucceeded++;
                        callbacksQueued++;
                        break;
                    }

                    case ProviderRefundLifecycleStatus.Failed:
                    {
                        var reason = FailureReason.Create(
                            providerStatus.ErrorCode,
                            providerStatus.ErrorMessage
                            ?? "Provider marked refund as failed during reconciliation.");

                        refund.MarkFailed(reason, clock.UtcNow);
                        await refundRepository.UpdateAsync(refund, cancellationToken);

                        if (payment.Status == PaymentStatus.RefundPending)
                        {
                            payment.MarkRefundFailed(refund.Id, reason, clock.UtcNow);
                            await paymentRepository.UpdateAsync(payment, cancellationToken);
                        }

                        await orderCallbackQueueService.QueueRefundFailedAsync(payment, refund, reason, cancellationToken);

                        refundsFailed++;
                        callbacksQueued++;
                        break;
                    }
                }

                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to save reconciliation result for refund {RefundId}; skipping",
                        refund.Id.Value);

                    unitOfWork.DetachUncommittedChanges();
                }
            }

            return Result<ReconciliationResultDto>.Success(new ReconciliationResultDto(
                PaymentsChecked: paymentsChecked,
                PaymentsSucceeded: paymentsSucceeded,
                PaymentsFailed: paymentsFailed,
                RefundsChecked: refundsChecked,
                RefundsSucceeded: refundsSucceeded,
                RefundsFailed: refundsFailed,
                CallbacksQueued: callbacksQueued));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReconcilePendingPayments failed unexpectedly");
            return Result<ReconciliationResultDto>.Failure(
                "Unexpected error while reconciling pending payments and refunds.");
        }
    }
}