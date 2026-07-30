using System.Net;
using Grpc.Core;
using NSubstitute;
using OpsConsole.UnitTests.TestHelpers;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class DeadLetterMutationEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public DeadLetterMutationEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Requeue_ShouldReturn403_WhenOperatorIsOpsViewerOnly()
    {
        using var client = _factory.CreateAuthorizedClient("OpsViewer");

        var response = await client.PostAsync($"/api/deadletters/{Guid.NewGuid()}/requeue", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Requeue_ShouldReturn200_WhenSuccessful()
    {
        _factory.OrderClient
            .RequeueDeadLetterAsync(Arg.Any<RequeueDeadLetterRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new MutationResult { Success = true, Message = "Message moved back to the outbox." }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.PostAsync($"/api/deadletters/{Guid.NewGuid()}/requeue", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requeue_ShouldReturn409_WhenUpstreamReportsFailure()
    {
        _factory.OrderClient
            .RequeueDeadLetterAsync(Arg.Any<RequeueDeadLetterRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new MutationResult { Success = false, Message = "Dead letter message already resolved." }));

        using var client = _factory.CreateAuthorizedClient("Admin");

        var response = await client.PostAsync($"/api/deadletters/{Guid.NewGuid()}/requeue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
