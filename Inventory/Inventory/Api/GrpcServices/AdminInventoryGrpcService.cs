using Application.Interfaces;
using Grpc.Core;
using Protos.AdminOps;

namespace Api.GrpcServices;

// Internal-only admin surface consumed by the Ops Console service (Phase 6:
// cross-service correlation on the saga detail page). Never routed through
// the Gateway. Mirrors Order's AdminOpsGrpcService auth pattern exactly:
// fail-closed shared secret via x-internal-api-key / InternalServices:OpsConsoleApiKey.
public class AdminInventoryGrpcService(
    IInventoryReservationStore reservationStore,
    IConfiguration configuration,
    ILogger<AdminInventoryGrpcService> logger)
    : AdminInventoryService.AdminInventoryServiceBase
{
    private const string ApiKeyHeader = "x-internal-api-key";

    public override async Task<GetReservationByOrderIdResponse> GetReservationByOrderId(
        GetReservationByOrderIdRequest request,
        ServerCallContext context)
    {
        EnsureAuthorized(context);

        if (!Guid.TryParse(request.OrderId, out var orderId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id must be a valid GUID."));
        }

        var reservation = await reservationStore.GetByOrderIdAsync(orderId, context.CancellationToken);

        if (reservation is null)
        {
            return new GetReservationByOrderIdResponse { Found = false };
        }

        var response = new GetReservationByOrderIdResponse
        {
            Found = true,
            ReservationId = reservation.ReservationId.ToString(),
            Status = reservation.Status,
            CreatedAt = reservation.CreatedAtUtc.ToString("O"),
            UpdatedAt = reservation.UpdatedAtUtc.ToString("O")
        };
        response.Items.AddRange(reservation.Items.Select(i => new Protos.AdminOps.ReservationItemSummary
        {
            ProductId = i.ProductId.ToString(),
            Quantity = i.Quantity
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
