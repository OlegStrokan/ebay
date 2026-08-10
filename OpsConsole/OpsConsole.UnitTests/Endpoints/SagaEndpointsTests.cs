using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using NSubstitute;
using OpsConsole.UnitTests.TestHelpers;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class SagaEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public SagaEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetSagas_ShouldReturn401_WhenApiKeyMissing()
    {
        using var client = _factory.CreateClientWithoutApiKey("Admin");

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSagas_ShouldReturn401_WhenJwtMissing()
    {
        using var client = _factory.CreateClientWithoutJwt();

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("SomeOtherIssuer")]
    [InlineData("")]
    public async Task GetSagas_ShouldReturn401_WhenTokenIssuerDoesNotMatch(string issuer)
    {
        using var client = _factory.CreateClientWithJwtIssuer(issuer, "Admin");

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSagas_ShouldReturn403_WhenOperatorLacksRequiredRole()
    {
        using var client = _factory.CreateAuthorizedClient("SomeOtherRole");

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("SuperAdmin")]
    [InlineData("OpsViewer")]
    public async Task GetSagas_ShouldReturn200_WhenOperatorHasQualifyingRole(string role)
    {
        _factory.OrderClient
            .GetSagasAsync(Arg.Any<GetSagasRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagasResponse { TotalCount = 0 }));

        using var client = _factory.CreateAuthorizedClient(role);

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSagas_ShouldMapQueryParams_AndReturnClientResponse()
    {
        GetSagasRequest? captured = null;
        _factory.OrderClient
            .GetSagasAsync(Arg.Do<GetSagasRequest>(r => captured = r), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagasResponse { TotalCount = 1 }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync("/api/sagas?status=Running&sagaType=OrderSaga&search=abc&skip=10&take=5");
        var body = await response.Content.ReadFromJsonAsync<GetSagasHttpResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal("Running", captured!.Status);
        Assert.Equal("OrderSaga", captured.SagaType);
        Assert.Equal("abc", captured.Search);
        Assert.Equal(10, captured.Skip);
        Assert.Equal(5, captured.Take);
        Assert.Equal(1, body!.TotalCount);
    }

    [Fact]
    public async Task GetSaga_ShouldReturn404_WhenNotFound()
    {
        _factory.OrderClient
            .GetSagaAsync(Arg.Any<GetSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaResponse { Found = false }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSaga_ShouldReturn200_WhenFound()
    {
        var sagaId = Guid.NewGuid().ToString();
        _factory.OrderClient
            .GetSagaAsync(Arg.Any<GetSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaResponse { Found = true, Id = sagaId, Status = "Running" }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{sagaId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSagaEvents_ShouldReturn200()
    {
        _factory.OrderClient
            .GetSagaEventsAsync(Arg.Any<GetSagaEventsRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaEventsResponse()));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{Guid.NewGuid()}/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSagaEvents_ShouldRedactPii_InRequestAndResponse()
    {
        var rawRequest = """{"CustomerId":"cust-1","DeliveryAddress":{"Street":"123 Main St","City":"Metropolis"},"TotalAmount":59.98}""";
        var rawResponse = """{"PaymentId":"pay-1","ProviderPaymentIntentId":"pi_123"}""";

        _factory.OrderClient
            .GetSagaEventsAsync(Arg.Any<GetSagaEventsRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetSagaEventsResponse
            {
                Steps = { new SagaStepEvent { StepName = "AuthorizePayment", Request = rawRequest, Response = rawResponse } }
            }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.GetAsync($"/api/sagas/{Guid.NewGuid()}/events");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("cust-1", body);
        Assert.DoesNotContain("123 Main St", body);
        Assert.DoesNotContain("pi_123", body);
        Assert.Contains("59.98", body, StringComparison.Ordinal); // amounts stay visible for triage
    }

    private sealed record GetSagasHttpResponse(int TotalCount);
}
