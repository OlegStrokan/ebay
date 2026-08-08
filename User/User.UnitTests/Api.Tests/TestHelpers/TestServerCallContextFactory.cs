using Grpc.Core;

namespace Api.Tests.TestHelpers;

// A bare Substitute.For<ServerCallContext>() leaves RequestHeaders null, which NREs in any
// handler that reads a header (e.g. GetUserByEmail's x-internal-api-key check).
internal sealed class FakeServerCallContext(Metadata requestHeaders) : ServerCallContext
{
    protected override string MethodCore => "test-method";
    protected override string HostCore => "test-host";
    protected override string PeerCore => "test-peer";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore => requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore { get; } = new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }

    protected override AuthContext AuthContextCore =>
        new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}

internal static class TestServerCallContextFactory
{
    public static ServerCallContext Create(Metadata? requestHeaders = null) =>
        new FakeServerCallContext(requestHeaders ?? new Metadata());
}
