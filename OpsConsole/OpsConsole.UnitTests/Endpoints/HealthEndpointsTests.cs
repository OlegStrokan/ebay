using System.Net;
using OpsConsole.UnitTests.TestHelpers;

namespace OpsConsole.UnitTests.Endpoints;

public class HealthEndpointsTests : IClassFixture<OpsConsoleWebApplicationFactory>
{
    private readonly OpsConsoleWebApplicationFactory _factory;

    public HealthEndpointsTests(OpsConsoleWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ShouldReturn200_WithNoAuthHeadersAtAll(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
