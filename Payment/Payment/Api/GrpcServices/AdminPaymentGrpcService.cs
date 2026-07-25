using System.Globalization;
using Domain.Interfaces;
using Grpc.Core;
using Protos.AdminOps;

namespace Api.GrpcServices;

public class AdminPaymentGrpcService(
    IPaymentRepository paymentRepository,
    IConfiguration configuration,
    ILogger<AdminPaymentGrpcService> logger)
    : AdminPaymentService.AdminPaymentServiceBase
{
    private const string ApiKeyHeader = "x-internal-api-key";

    public override async Task<GetPaymentsByOrderIdResponse> GetPaymentsByOrderId(
        GetPaymentsByOrderIdRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id is required."));
        }

        var payments = await paymentRepository.GetAllByOrderIdAsync(request.OrderId, context.CancellationToken);

        var response = new GetPaymentsByOrderIdResponse();
        response.Payments.AddRange(payments.Select(p => new PaymentSummary
        {
            PaymentId = p.Id.ToString(),
            Status = p.Status.ToString(),
            Amount = p.Amount.Amount.ToString(CultureInfo.InvariantCulture),
            Currency = p.Amount.Currency,
            TotalRefundedAmount = p.TotalRefundedAmount.ToString(CultureInfo.InvariantCulture),
            ProviderPaymentIntentId = p.ProviderPaymentIntentId?.ToString() ?? string.Empty,
            CreatedAt = p.CreatedAt.ToString("O"),
            UpdatedAt = p.UpdatedAt.ToString("O")
        }));
        return response;
    }

    private void EnsureAuthorized(ServerCallContext context)
    {
        var expectedKey = configuration["InternalServices:OpsConsoleApiKey"];

        if (string.IsNullOrEmpty(expectedKey))
        {
            logger.LogError("InternalServices:OpsConsoleApiKey is not configured; rejecting admin ops call.");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }

        var providedKey = context.RequestHeaders.GetValue(ApiKeyHeader);

        if (providedKey != expectedKey)
        {
            logger.LogWarning("Rejected admin ops call with missing/invalid {Header}.", ApiKeyHeader);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }
    }
}
