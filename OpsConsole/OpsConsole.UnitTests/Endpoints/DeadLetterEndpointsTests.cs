using System.Net;
using Grpc.Core;
using NSubstitute;
using OpsConsole.UnitTests.TestHelpers;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class DeadLetterEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public DeadLetterEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDeadLetters_ShouldReturn401_WhenApiKeyMissing()
    {
        using var client = _factory.CreateClientWithoutApiKey("Admin");

        var response = await client.GetAsync("/api/deadletters");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDeadLetters_ShouldReturn403_WhenOperatorLacksRequiredRole()
    {
        using var client = _factory.CreateAuthorizedClient("SomeOtherRole");

        var response = await client.GetAsync("/api/deadletters");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDeadLetters_ShouldReturn200_WhenOperatorIsOpsViewer()
    {
        GetDeadLettersRequest? captured = null;
        _factory.OrderClient
            .GetDeadLettersAsync(Arg.Do<GetDeadLettersRequest>(r => captured = r), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new GetDeadLettersResponse()));

        using var client = _factory.CreateAuthorizedClient("OpsViewer");

        var response = await client.GetAsync("/api/deadletters?skip=5&take=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(5, captured!.Skip);
        Assert.Equal(10, captured.Take);
    }
}
