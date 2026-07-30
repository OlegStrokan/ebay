using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using OpsConsole.Grpc;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Grpc;

public class InternalApiKeyInterceptorTests
{
    private static Method<GetSagaRequest, GetSagaResponse> CreateMethod() => new(
        MethodType.Unary,
        "admin_ops.AdminOpsService",
        "GetSaga",
        Marshallers.Create<GetSagaRequest>(r => r.ToByteArray(), GetSagaRequest.Parser.ParseFrom),
        Marshallers.Create<GetSagaResponse>(r => r.ToByteArray(), GetSagaResponse.Parser.ParseFrom));

    [Fact]
    public void AsyncUnaryCall_ShouldAttachConfiguredInternalApiKeyHeader()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServices:OpsConsoleApiKey"] = "shared-secret"
            })
            .Build();

        var interceptor = new InternalApiKeyInterceptor(config);
        var context = new ClientInterceptorContext<GetSagaRequest, GetSagaResponse>(
            CreateMethod(), host: null, new CallOptions());

        Metadata? capturedHeaders = null;

        var call = interceptor.AsyncUnaryCall(
            new GetSagaRequest { SagaId = "saga-1" },
            context,
            (request, ctx) =>
            {
                capturedHeaders = ctx.Options.Headers;
                return GrpcCall(new GetSagaResponse());
            });

        Assert.NotNull(capturedHeaders);
        var entry = Assert.Single(capturedHeaders!, h => h.Key == "x-internal-api-key");
        Assert.Equal("shared-secret", entry.Value);

        call.Dispose();
    }

    [Fact]
    public void AsyncUnaryCall_ShouldPreserveExistingHeaders()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServices:OpsConsoleApiKey"] = "shared-secret"
            })
            .Build();

        var interceptor = new InternalApiKeyInterceptor(config);
        var existingHeaders = new Metadata { { "x-request-id", "abc-123" } };
        var context = new ClientInterceptorContext<GetSagaRequest, GetSagaResponse>(
            CreateMethod(), host: null, new CallOptions(headers: existingHeaders));

        Metadata? capturedHeaders = null;

        var call = interceptor.AsyncUnaryCall(
            new GetSagaRequest { SagaId = "saga-1" },
            context,
            (request, ctx) =>
            {
                capturedHeaders = ctx.Options.Headers;
                return GrpcCall(new GetSagaResponse());
            });

        Assert.NotNull(capturedHeaders);
        Assert.Contains(capturedHeaders!, h => h.Key == "x-request-id" && h.Value == "abc-123");
        Assert.Contains(capturedHeaders!, h => h.Key == "x-internal-api-key" && h.Value == "shared-secret");

        call.Dispose();
    }

    [Fact]
    public void AsyncUnaryCall_ShouldSendEmptyKey_WhenNotConfigured()
    {
        // Fail-closed on the server side: an empty key here means the downstream
        // AdminOpsGrpcService rejects the call outright rather than silently allowing it.
        var config = new ConfigurationBuilder().Build();
        var interceptor = new InternalApiKeyInterceptor(config);
        var context = new ClientInterceptorContext<GetSagaRequest, GetSagaResponse>(
            CreateMethod(), host: null, new CallOptions());

        Metadata? capturedHeaders = null;

        var call = interceptor.AsyncUnaryCall(
            new GetSagaRequest(),
            context,
            (request, ctx) =>
            {
                capturedHeaders = ctx.Options.Headers;
                return GrpcCall(new GetSagaResponse());
            });

        var entry = Assert.Single(capturedHeaders!, h => h.Key == "x-internal-api-key");
        Assert.Equal(string.Empty, entry.Value);

        call.Dispose();
    }
}
