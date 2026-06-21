using Application.DTOs;
using Domain.Entities;
using Domain.Enums;

namespace Application.Mappers;

internal static class PaymentDtoMapper
{
    public static PaymentDetailsDto ToPaymentDetailsDto(Payment payment)
    {
        return new PaymentDetailsDto(
            PaymentId: payment.Id.Value,
            OrderId: payment.OrderId,
            CustomerId: payment.CustomerId,
            Amount: payment.Amount.Amount,
            Currency: payment.Amount.Currency,
            PaymentMethod: payment.Method,
            Status: payment.Status,
            ProviderPaymentIntentId: payment.ProviderPaymentIntentId?.Value,
            ProviderRefundId: payment.ProviderRefundId?.Value,
            FailureCode: payment.FailureReason?.Code,
            FailureMessage: payment.FailureReason?.Message,
            CreatedAt: payment.CreatedAt,
            UpdatedAt: payment.UpdatedAt,
            SucceededAt: payment.SucceededAt,
            FailedAt: payment.FailedAt);
    }

    public static ProcessPaymentResultDto ToProcessPaymentResult(
        Payment payment,
        ProcessPaymentStatus? overrideStatus = null,
        string? clientSecret = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var status = overrideStatus ?? ToProcessPaymentStatus(payment.Status);
        return new ProcessPaymentResultDto(
            PaymentId: payment.Id.Value,
            Status: status,
            ProviderPaymentIntentId: payment.ProviderPaymentIntentId?.Value,
            ClientSecret: clientSecret,
            ErrorCode: errorCode ?? payment.FailureReason?.Code,
            ErrorMessage: errorMessage ?? payment.FailureReason?.Message);
    }

    public static RefundPaymentResultDto ToRefundPaymentResult(
        Payment payment,
        Refund refund,
        RefundPaymentStatus? overrideStatus = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var status = overrideStatus ?? ToRefundPaymentStatus(refund.Status);
        return new RefundPaymentResultDto(
            PaymentId: payment.Id.Value,
            RefundId: refund.Id.Value,
            Status: status,
            ProviderRefundId: refund.ProviderRefundId?.Value,
            ErrorCode: errorCode ?? refund.FailureReason?.Code,
            ErrorMessage: errorMessage ?? refund.FailureReason?.Message);
    }

    public static ProcessPaymentStatus ToProcessPaymentStatus(PaymentStatus paymentStatus)
    {
        return paymentStatus switch
        {
            PaymentStatus.Created => ProcessPaymentStatus.Pending,
            PaymentStatus.PendingProviderConfirmation => ProcessPaymentStatus.Pending,
            PaymentStatus.Authorized => ProcessPaymentStatus.Authorized,
            PaymentStatus.Succeeded => ProcessPaymentStatus.Succeeded,
            PaymentStatus.Failed => ProcessPaymentStatus.Failed,
            PaymentStatus.RefundPending => ProcessPaymentStatus.Succeeded,
            PaymentStatus.Refunded => ProcessPaymentStatus.Succeeded,
            PaymentStatus.RefundFailed => ProcessPaymentStatus.Succeeded,
            _ => ProcessPaymentStatus.Pending,
        };
    }

    public static RefundPaymentStatus ToRefundPaymentStatus(RefundStatus refundStatus)
    {
        return refundStatus switch
        {
            RefundStatus.Requested => RefundPaymentStatus.Pending,
            RefundStatus.PendingProviderConfirmation => RefundPaymentStatus.Pending,
            RefundStatus.Succeeded => RefundPaymentStatus.Succeeded,
            RefundStatus.Failed => RefundPaymentStatus.Failed,
            _ => RefundPaymentStatus.Pending,
        };
    }
}