using Api.Mappers;
using Application.Commands.CapturePayment;
using Application.Commands.ProcessPayment;
using Application.Commands.RefundPayment;
using Application.DTOs;
using Application.Queries.GetPaymentById;
using Application.Queries.GetPaymentByOrderId;
using Grpc.Core;
using MediatR;
using Protos.Payment;
using System.Security.Cryptography;
using System.Text;
using ApplicationProcessPaymentStatus = Application.DTOs.ProcessPaymentStatus;
using ApplicationRefundPaymentStatus = Application.DTOs.RefundPaymentStatus;
using DomainPaymentStatus = Domain.Enums.PaymentStatus;
using GrpcProcessPaymentStatus = Protos.Payment.ProcessPaymentStatus;
using GrpcRefundPaymentStatus = Protos.Payment.RefundPaymentStatus;
using GrpcPaymentRecordStatus = Protos.Payment.PaymentRecordStatus;

namespace Api.GrpcServices;

public sealed class PaymentGrpcService(
    IMediator mediator,
    ILogger<PaymentGrpcService> logger)
    : PaymentService.PaymentServiceBase
{
    public override async Task<ProcessPaymentResponse> ProcessPayment(
        ProcessPaymentRequest request,
        ServerCallContext context)
    {
        var amount = request.Amount?.ToDecimal() ?? 0m;
        var currency = NormalizeCurrency(request.Currency);
        var idempotencyKey = ResolveIdempotencyKey(
            request.IdempotencyKey,
            "grpc-process",
            $"{request.OrderId}|{request.CustomerId}|{amount:F4}|{currency}");

        var command = new ProcessPaymentCommand(
            OrderId: request.OrderId,
            CustomerId: request.CustomerId,
            Amount: amount,
            Currency: currency,
            PaymentMethod: PaymentMethodMapper.FromGrpc(request.PaymentMethod),
            IdempotencyKey: idempotencyKey,
            CustomerEmail: EmptyToNull(request.CustomerEmail));

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "ProcessPayment gRPC request failed for OrderId={OrderId}. Errors={Errors}",
                request.OrderId,
                JoinErrors(result.Errors));
            return BuildProcessPaymentFailure(result.Errors);
        }

        logger.LogInformation(
            "ProcessPayment gRPC request completed. OrderId={OrderId}, PaymentId={PaymentId}, Status={Status}",
            request.OrderId,
            result.Value.PaymentId,
            result.Value.Status);

        return new ProcessPaymentResponse
        {
            Success = result.Value.Status != ApplicationProcessPaymentStatus.Failed,
            PaymentId = result.Value.PaymentId,
            ErrorCode = result.Value.ErrorCode ?? string.Empty,
            ErrorMessage = result.Value.ErrorMessage ?? string.Empty,
            Status = MapProcessPaymentStatus(result.Value.Status),
            ProviderPaymentIntentId = result.Value.ProviderPaymentIntentId ?? string.Empty,
            ClientSecret = result.Value.ClientSecret ?? string.Empty,
        };
    }

    public override async Task<RefundPaymentResponse> RefundPayment(
        RefundPaymentRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PaymentId))
        {
            return BuildRefundPaymentFailure(["PaymentId is required"], request.PaymentId);
        }

        var amount = request.Amount?.ToDecimal() ?? 0m;
        var paymentCurrency = NormalizeCurrency(request.Currency);

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            var paymentResult = await mediator.Send(
                new GetPaymentByIdQuery(request.PaymentId),
                context.CancellationToken);

            if (!paymentResult.IsSuccess || paymentResult.Value is null)
            {
                return BuildRefundPaymentFailure(
                    paymentResult.Errors.Count > 0
                        ? paymentResult.Errors
                        : ["Failed to resolve payment currency for refund"],
                    request.PaymentId);
            }

            paymentCurrency = NormalizeCurrency(paymentResult.Value.Currency);
        }

        var idempotencyKey = ResolveIdempotencyKey(
            request.IdempotencyKey,
            "grpc-refund",
            $"{request.PaymentId}|{amount:F4}|{paymentCurrency}|{request.Reason}");

        var command = new RefundPaymentCommand(
            PaymentId: request.PaymentId,
            Amount: amount,
            Currency: paymentCurrency,
            Reason: request.Reason,
            IdempotencyKey: idempotencyKey);

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "RefundPayment gRPC request failed for PaymentId={PaymentId}. Errors={Errors}",
                request.PaymentId,
                JoinErrors(result.Errors));
            return BuildRefundPaymentFailure(result.Errors, request.PaymentId);
        }

        logger.LogInformation(
            "RefundPayment gRPC request completed. PaymentId={PaymentId}, RefundId={RefundId}, Status={Status}",
            result.Value.PaymentId,
            result.Value.RefundId,
            result.Value.Status);

        return new RefundPaymentResponse
        {
            Success = result.Value.Status != ApplicationRefundPaymentStatus.Failed,
            PaymentId = result.Value.PaymentId,
            RefundId = result.Value.RefundId,
            ErrorCode = result.Value.ErrorCode ?? string.Empty,
            ErrorMessage = result.Value.ErrorMessage ?? string.Empty,
            ProviderRefundId = result.Value.ProviderRefundId ?? string.Empty,
            Status = MapRefundPaymentStatus(result.Value.Status),
        };
    }

    public override async Task<GetPaymentResponse> GetPayment(
        GetPaymentRequest request,
        ServerCallContext context)
    {
        var result = await mediator.Send(
            new GetPaymentByIdQuery(request.PaymentId),
            context.CancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return new GetPaymentResponse
            {
                Success = false,
                ErrorMessage = JoinErrors(result.Errors),
            };
        }

        return new GetPaymentResponse
        {
            Success = true,
            Payment = MapPaymentDetails(result.Value),
        };
    }

    public override async Task<GetPaymentByOrderAndIdempotencyResponse> GetPaymentByOrderAndIdempotency(
        GetPaymentByOrderAndIdempotencyRequest request,
        ServerCallContext context)
    {
        var idempotencyKey = ResolveIdempotencyKey(
            request.IdempotencyKey,
            "grpc-order-idem",
            $"{request.OrderId}|default");

        var result = await mediator.Send(
            new GetPaymentByOrderIdQuery(request.OrderId, idempotencyKey),
            context.CancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return new GetPaymentByOrderAndIdempotencyResponse
            {
                Success = false,
                ErrorMessage = JoinErrors(result.Errors),
            };
        }

        return new GetPaymentByOrderAndIdempotencyResponse
        {
            Success = true,
            Payment = MapPaymentDetails(result.Value),
        };
    }

    public override async Task<CapturePaymentResponse> CapturePayment(
        CapturePaymentRequest request,
        ServerCallContext context)
    {
        var amount = request.Amount?.ToDecimal() ?? 0m;
        var currency = NormalizeCurrency(request.Currency);
        var idempotencyKey = ResolveIdempotencyKey(
            request.IdempotencyKey,
            "grpc-capture",
            $"{request.OrderId}|{request.ProviderPaymentIntentId}|{amount:F4}|{currency}");

        var command = new CapturePaymentCommand(
            OrderId: request.OrderId,
            CustomerId: request.CustomerId,
            ProviderPaymentIntentId: request.ProviderPaymentIntentId,
            Amount: amount,
            Currency: currency,
            IdempotencyKey: idempotencyKey);

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "CapturePayment gRPC request failed for OrderId={OrderId}. Errors={Errors}",
                request.OrderId,
                JoinErrors(result.Errors));

            return new CapturePaymentResponse
            {
                Success = false,
                Status = GrpcProcessPaymentStatus.Failed,
                ErrorCode = "CAPTURE_PAYMENT_FAILED",
                ErrorMessage = JoinErrors(result.Errors),
            };
        }

        logger.LogInformation(
            "CapturePayment gRPC request completed. OrderId={OrderId}, PaymentId={PaymentId}, Status={Status}",
            request.OrderId,
            result.Value.PaymentId,
            result.Value.Status);

        return new CapturePaymentResponse
        {
            Success = result.Value.Status != ApplicationProcessPaymentStatus.Failed,
            PaymentId = result.Value.PaymentId,
            ErrorCode = result.Value.ErrorCode ?? string.Empty,
            ErrorMessage = result.Value.ErrorMessage ?? string.Empty,
            Status = MapProcessPaymentStatus(result.Value.Status),
            ProviderPaymentIntentId = result.Value.ProviderPaymentIntentId ?? string.Empty,
        };
    }

    public override async Task<CancelAuthorizationResponse> CancelAuthorization(
        CancelAuthorizationRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderPaymentIntentId))
        {
            return new CancelAuthorizationResponse
            {
                Success = false,
                // @todo: create enum for all error code messages
                ErrorCode = "MISSING_PROVIDER_PAYMENT_INTENT_ID",
                ErrorMessage = "ProviderPaymentIntentId is required.",
            };
        }

        try
        {
            // i dont give a shit about idempotency here
            var stripeProvider = context.GetHttpContext().RequestServices
                .GetRequiredService<Application.Gateways.IStripePaymentProvider>();

            await stripeProvider.CancelAuthorizationAsync(
                request.ProviderPaymentIntentId,
                context.CancellationToken);

            logger.LogInformation(
                "CancelAuthorization gRPC completed. ProviderPaymentIntentId={Id}",
                request.ProviderPaymentIntentId);

            return new CancelAuthorizationResponse { Success = true };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CancelAuthorization gRPC failed. ProviderPaymentIntentId={Id}",
                request.ProviderPaymentIntentId);

            return new CancelAuthorizationResponse
            {
                Success = false,
                ErrorCode = "CANCEL_AUTHORIZATION_FAILED",
                ErrorMessage = ex.Message,
            };
        }
    }


    //@todo: move helpers to new file
    private static ProcessPaymentResponse BuildProcessPaymentFailure(IReadOnlyCollection<string> errors)
    {
        return new ProcessPaymentResponse
        {
            Success = false,
            Status = GrpcProcessPaymentStatus.Failed,
            ErrorCode = "PROCESS_PAYMENT_FAILED",
            ErrorMessage = JoinErrors(errors),
        };
    }

    private static RefundPaymentResponse BuildRefundPaymentFailure(IReadOnlyCollection<string> errors, string? paymentId)
    {
        return new RefundPaymentResponse
        {
            Success = false,
            PaymentId = paymentId ?? string.Empty,
            Status = GrpcRefundPaymentStatus.Failed,
            ErrorCode = "REFUND_PAYMENT_FAILED",
            ErrorMessage = JoinErrors(errors),
        };
    }

    private static PaymentDetails MapPaymentDetails(PaymentDetailsDto dto)
    {
        return new PaymentDetails
        {
            PaymentId = dto.PaymentId,
            OrderId = dto.OrderId,
            CustomerId = dto.CustomerId,
            Amount = dto.Amount.ToDecimalValue(),
            Currency = dto.Currency,
            PaymentMethod = PaymentMethodMapper.ToGrpc(dto.PaymentMethod),
            Status = MapPaymentRecordStatus(dto.Status),
            ProviderPaymentIntentId = dto.ProviderPaymentIntentId ?? string.Empty,
            ProviderRefundId = dto.ProviderRefundId ?? string.Empty,
            FailureCode = dto.FailureCode ?? string.Empty,
            FailureMessage = dto.FailureMessage ?? string.Empty,
            CreatedAtUnix = ToUnixSeconds(dto.CreatedAt),
            UpdatedAtUnix = ToUnixSeconds(dto.UpdatedAt),
            SucceededAtUnix = ToUnixSeconds(dto.SucceededAt),
            FailedAtUnix = ToUnixSeconds(dto.FailedAt),
        };
    }

    private static GrpcProcessPaymentStatus MapProcessPaymentStatus(ApplicationProcessPaymentStatus status)
    {
        return status switch
        {
            ApplicationProcessPaymentStatus.Succeeded => GrpcProcessPaymentStatus.Succeeded,
            ApplicationProcessPaymentStatus.Pending => GrpcProcessPaymentStatus.Pending,
            ApplicationProcessPaymentStatus.RequiresAction => GrpcProcessPaymentStatus.RequiresAction,
            ApplicationProcessPaymentStatus.Failed => GrpcProcessPaymentStatus.Failed,
            _ => GrpcProcessPaymentStatus.Unspecified,
        };
    }

    private static GrpcRefundPaymentStatus MapRefundPaymentStatus(ApplicationRefundPaymentStatus status)
    {
        return status switch
        {
            ApplicationRefundPaymentStatus.Succeeded => GrpcRefundPaymentStatus.Succeeded,
            ApplicationRefundPaymentStatus.Pending => GrpcRefundPaymentStatus.Pending,
            ApplicationRefundPaymentStatus.Failed => GrpcRefundPaymentStatus.Failed,
            _ => GrpcRefundPaymentStatus.Unspecified,
        };
    }

    private static GrpcPaymentRecordStatus MapPaymentRecordStatus(DomainPaymentStatus status)
    {
        return status switch
        {
            DomainPaymentStatus.Created => GrpcPaymentRecordStatus.Created,
            DomainPaymentStatus.PendingProviderConfirmation => GrpcPaymentRecordStatus.PendingProviderConfirmation,
            DomainPaymentStatus.Succeeded => GrpcPaymentRecordStatus.Succeeded,
            DomainPaymentStatus.Failed => GrpcPaymentRecordStatus.Failed,
            DomainPaymentStatus.RefundPending => GrpcPaymentRecordStatus.RefundPending,
            DomainPaymentStatus.Refunded => GrpcPaymentRecordStatus.Refunded,
            DomainPaymentStatus.RefundFailed => GrpcPaymentRecordStatus.RefundFailed,
            _ => GrpcPaymentRecordStatus.Unspecified,
        };
    }

    private static string ResolveIdempotencyKey(string? rawValue, string prefix, string fallbackSeed)
    {
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            return rawValue.Trim();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fallbackSeed));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "USD";
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static long ToUnixSeconds(DateTime? value)
    {
        return value is null ? 0 : new DateTimeOffset(value.Value).ToUnixTimeSeconds();
    }

    private static string JoinErrors(IReadOnlyCollection<string> errors)
    {
        if (errors.Count == 0)
        {
            return "No additional error details were provided";
        }

        return string.Join("; ", errors);
    }
}