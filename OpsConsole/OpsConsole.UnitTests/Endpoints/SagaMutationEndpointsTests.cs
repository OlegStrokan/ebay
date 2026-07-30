using System.Net;
using Grpc.Core;
using NSubstitute;
using OpsConsole.UnitTests.TestHelpers;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class SagaMutationEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public SagaMutationEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Compensate_ShouldReturn403_WhenOperatorIsOpsViewerOnly()
    {
        // OpsViewer can read, but only OpsAdmin (Admin/SuperAdmin) may mutate.
        using var client = _factory.CreateAuthorizedClient("OpsViewer");

        var response = await client.PostAsync($"/api/sagas/{Guid.NewGuid()}/compensate", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Compensate_ShouldReturn200_WhenSuccessful()
    {
        _factory.OrderClient
            .CompensateSagaAsync(Arg.Any<CompensateSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new MutationResult { Success = true, Message = "Compensation completed." }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.PostAsync($"/api/sagas/{Guid.NewGuid()}/compensate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Compensate_ShouldReturn409_WhenUpstreamReportsFailure()
    {
        _factory.OrderClient
            .CompensateSagaAsync(Arg.Any<CompensateSagaRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new MutationResult { Success = false, Message = "Saga is currently locked." }));

        using var client = _factory.CreateAuthorizedClient("SuperAdmin");

        var response = await client.PostAsync($"/api/sagas/{Guid.NewGuid()}/compensate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RetryCompensation_ShouldReturn200_WhenSuccessful()
    {
        _factory.OrderClient
            .RetryCompensationAsync(Arg.Any<RetryCompensationRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new MutationResult { Success = true, Message = "Compensation retry scheduled." }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.PostAsync($"/api/sagas/{Guid.NewGuid()}/retry-compensation", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RetryCompensation_ShouldReturn401_WhenApiKeyMissing()
    {
        using var client = _factory.CreateClientWithoutApiKey("Admin");

        var response = await client.PostAsync($"/api/sagas/{Guid.NewGuid()}/retry-compensation", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
