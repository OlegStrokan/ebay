using Application.DTOs;
using Application.Gateways;
using Grpc.Core;
using Infrastructure.Extensions;
using Protos.Accounting;

namespace Infrastructure.Gateways;

public class AccountingGateway(
    AccountingService.AccountingServiceClient client,
    ILogger<AccountingGateway> logger) : IAccountingGateway
{
    public async Task<string> RecordRefundAsync(
        Guid orderId,
        string refundId,
        decimal amount,
        string currency,
        string reason,
        CancellationToken cancellationToken)
    {



        var request = new RecordRefundRequest
        {
            OrderId = orderId.ToString(),
            RefundId = refundId,
            Amount = amount.ToDecimalValue(),
            Currency = currency,
            Reason = reason
        };

        try
        {
            var response = await client.RecordRefundAsync(request, cancellationToken: cancellationToken);

            if (!response.Success)
                throw new InvalidOperationException(
                    $"Recording refund failed. OrderId={orderId}, RefundId={refundId}, Error={response.ErrorMessage}");

            logger.LogInformation(
                "Refund recorded in accounting. OrderId={OrderId}, RefundId={RefundId}, TransactionId={TransactionId}",
                orderId,
                refundId,
                response.TransactionId);

            return response.TransactionId;
        } 
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            throw new InvalidOperationException(
                $"Invalid refund data for OrderId={orderId}. Detail={ex.Status.Detail}");
        }
        
    }

    public async Task<string> ReverseRevenueAsync(
        Guid orderId,
        Guid returnRequestId,
        decimal amount,
        string currency,
        List<OrderItemDto> returnedItems,
        CancellationToken cancellationToken)
    {
        var request = new ReverseRevenueRequest
        {
            OrderId = orderId.ToString(),
            ReturnRequestId = returnRequestId.ToString(),
            Amount = amount.ToDecimalValue(),
            Currency = currency
        };

        request.ReturnedItems.AddRange(returnedItems.Select(i => new AccountingItem
        {
            ProductId = i.ProductId.ToString(),
            Quantity = i.Quantity,
            Price = i.Price.ToDecimalValue(),
            Currency = i.Currency
        }));

        try
        {
            var response = await client.ReverseRevenueAsync(request, cancellationToken: cancellationToken);

            if (!response.Success)
                throw new InvalidOperationException(
                    $"Revenue reversal failed. OrderId={orderId}, Amount={amount} {currency}, Error={response.ErrorMessage}");

            logger.LogInformation(
                "Revenue reversed in accounting. OrderId={OrderId}, ReturnRequestId={ReturnRequestId}, ReversalId={ReversalId}, Amount={Amount} {Currency}",
                orderId,
                returnRequestId,
                response.ReversalId,
                amount,
                currency);

            return response.ReversalId;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Order not found in accounting system. OrderId={orderId}. Detail={ex.Status.Detail}");
        }
    }

    public async Task CancelRevenueReversalAsync(
        string reversalId,
        string reason,
        CancellationToken cancellationToken)
    {
        var request = new CancelReversalRequest
        {
            ReversalId = reversalId,
            Reason = reason
        };

        try
        {
            var response = await client.CancelReversalAsync(request, cancellationToken: cancellationToken);

            if (!response.Success)
            {
                throw new InvalidOperationException(
                    $"Canceling revenue reversal failed. ReversalId={reversalId}, Error={response.ErrorMessage}");
            }

            logger.LogInformation(
                "Revenue reversal cancelling in accounting. ReversalId={ReversalId}",
                reversalId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // idempotent: already cancelled/not found
            logger.LogWarning(
                "CancelRevenueReversal: reversal not found (treated as idempotent success). ReversalId={ReversalId}",
                reversalId);
        }
    }
    


}