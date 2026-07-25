using Grpc.Core;
using Protos.AdminOps;

namespace OpsConsole.Endpoints;

public static class SagaCorrelationEndpoints
{
    public static void MapSagaCorrelationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sagas/{id}/correlation", async (
            string id,
            AdminOpsService.AdminOpsServiceClient orderClient,
            AdminPaymentService.AdminPaymentServiceClient paymentClient,
            AdminInventoryService.AdminInventoryServiceClient inventoryClient) =>
        {
            var saga = await orderClient.GetSagaAsync(new GetSagaRequest { SagaId = id });
            if (!saga.Found)
            {
                return Results.NotFound();
            }

            // saga.CorrelationId is the order id for both OrderSaga and ReturnSaga in this
            // codebase. Payment/Inventory calls are best-effort and independent of each
            // other — one service being down or having no data for this order shouldn't
            // hide the other's data.
            var payments = await SafeCallAsync(
                () => paymentClient.GetPaymentsByOrderIdAsync(new GetPaymentsByOrderIdRequest { OrderId = saga.CorrelationId }),
                new GetPaymentsByOrderIdResponse());

            var reservation = await SafeCallAsync(
                () => inventoryClient.GetReservationByOrderIdAsync(new GetReservationByOrderIdRequest { OrderId = saga.CorrelationId }),
                new GetReservationByOrderIdResponse { Found = false });

            return Results.Ok(new
            {
                orderTrackingId = saga.OrderTrackingId,
                payments = payments.Payments,
                reservation = reservation.Found
                    ? new
                    {
                        reservation.ReservationId,
                        reservation.Status,
                        reservation.CreatedAt,
                        reservation.UpdatedAt,
                        reservation.Items
                    }
                    : null
            });
        }).RequireAuthorization("OpsViewer");
    }

    private static async Task<T> SafeCallAsync<T>(Func<AsyncUnaryCall<T>> call, T fallback)
    {
        try
        {
            return await call();
        }
        catch (RpcException)
        {
            return fallback;
        }
    }
}
