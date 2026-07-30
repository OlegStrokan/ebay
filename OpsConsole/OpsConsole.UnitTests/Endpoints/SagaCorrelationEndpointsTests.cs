using System.Net;
using Grpc.Core;
using NSubstitute;
using OpsConsole.UnitTests.TestHelpers;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class SagaCorrelationEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public SagaCorrelationEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCorrelation_ShouldReturn404_WhenSagaNotFound()
    {
        _factory.OrderClient
            .GetSagaAsync(Arg.Any<GetSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaResponse { Found = false }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{Guid.NewGuid()}/correlation");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCorrelation_ShouldReturn200_AndNotHidePaymentsWhenInventoryFails()
    {
        var correlationId = Guid.NewGuid().ToString();
        _factory.OrderClient
            .GetSagaAsync(Arg.Any<GetSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaResponse { Found = true, CorrelationId = correlationId, OrderTrackingId = "TRACK-1" }));

        var paymentsResponse = new GetPaymentsByOrderIdResponse();
        paymentsResponse.Payments.Add(new PaymentSummary { PaymentId = "pay-1", Status = "Succeeded" });

        _factory.PaymentClient
            .GetPaymentsByOrderIdAsync(Arg.Any<GetPaymentsByOrderIdRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(paymentsResponse));

        // Inventory is down — GetReservationByOrderId throws. Payments must still come back.
        _factory.InventoryClient
            .GetReservationByOrderIdAsync(Arg.Any<GetReservationByOrderIdRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(_ => GrpcFail<GetReservationByOrderIdResponse>(StatusCode.Unavailable, "Inventory unreachable"));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{Guid.NewGuid()}/correlation");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("pay-1", body);
        Assert.Contains("TRACK-1", body);
    }
}
