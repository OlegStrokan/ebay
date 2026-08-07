using Api.Mappers;
using Application.Commands.CancelReversal;
using Application.Commands.RecordRefund;
using Application.Commands.ReverseRevenue;
using Grpc.Core;
using MediatR;
using Protos.Accounting;

namespace Api.GrpcServices;

public sealed class AccountingGrpcService(
    IMediator mediator,
    ILogger<AccountingGrpcService> logger)
    : AccountingService.AccountingServiceBase
{
    public override async Task<RecordRefundResponse> RecordRefund(
        RecordRefundRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrderId, out var orderId))
            return new RecordRefundResponse { Success = false, ErrorMessage = "A valid OrderId is required." };

        var command = new RecordRefundCommand(
            OrderId: orderId,
            RefundId: request.RefundId,
            Amount: request.Amount?.ToDecimal() ?? 0m,
            Currency: request.Currency,
            Reason: request.Reason);

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "RecordRefund gRPC request failed. OrderId={OrderId}, RefundId={RefundId}, Error={Error}",
                request.OrderId,
                request.RefundId,
                result.Error);
            return new RecordRefundResponse { Success = false, ErrorMessage = result.Error ?? "RecordRefund failed." };
        }

        return new RecordRefundResponse { Success = true, TransactionId = result.Value };
    }

    public override async Task<ReverseRevenueResponse> ReverseRevenue(
        ReverseRevenueRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrderId, out var orderId))
            return new ReverseRevenueResponse { Success = false, ErrorMessage = "A valid OrderId is required." };

        if (!Guid.TryParse(request.ReturnRequestId, out var returnRequestId) || returnRequestId == Guid.Empty)
            return new ReverseRevenueResponse
            {
                Success = false,
                ErrorMessage = "A valid ReturnRequestId is required — it is the idempotency key for the reversal."
            };

        var command = new ReverseRevenueCommand(
            OrderId: orderId,
            ReturnRequestId: returnRequestId,
            Amount: request.Amount?.ToDecimal() ?? 0m,
            Currency: request.Currency);

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            logger.LogWarning(
                "ReverseRevenue gRPC request failed. OrderId={OrderId}, ReturnRequestId={ReturnRequestId}, Error={Error}",
                request.OrderId,
                request.ReturnRequestId,
                result.Error);
            return new ReverseRevenueResponse { Success = false, ErrorMessage = result.Error ?? "ReverseRevenue failed." };
        }

        return new ReverseRevenueResponse { Success = true, ReversalId = result.Value };
    }

    public override async Task<CancelReversalResponse> CancelReversal(
        CancelReversalRequest request,
        ServerCallContext context)
    {
        var command = new CancelReversalCommand(request.ReversalId, request.Reason);

        var result = await mediator.Send(command, context.CancellationToken);
        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "CancelReversal gRPC request failed. ReversalId={ReversalId}, Error={Error}",
                request.ReversalId,
                result.Error);
            return new CancelReversalResponse { Success = false, ErrorMessage = result.Error ?? "CancelReversal failed." };
        }

        return new CancelReversalResponse { Success = true };
    }
}
