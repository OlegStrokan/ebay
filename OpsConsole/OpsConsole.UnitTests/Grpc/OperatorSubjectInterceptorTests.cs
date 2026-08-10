using System.Security.Claims;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using OpsConsole.Grpc;
using Protos.AdminOps;
using static OpsConsole.UnitTests.TestHelpers.GrpcTestHelpers;

namespace OpsConsole.UnitTests.Grpc;

public class OperatorSubjectInterceptorTests
{
    private static Method<GetSagaRequest, GetSagaResponse> CreateMethod() => new(
        MethodType.Unary,
        "admin_ops.AdminOpsService",
        "GetSaga",
        Marshallers.Create<GetSagaRequest>(r => r.ToByteArray(), GetSagaRequest.Parser.ParseFrom),
        Marshallers.Create<GetSagaResponse>(r => r.ToByteArray(), GetSagaResponse.Parser.ParseFrom));

    private static IHttpContextAccessor CreateAccessor(ClaimsPrincipal? user)
    {
        var httpContext = new DefaultHttpContext();
        if (user is not null) httpContext.User = user;
        return new HttpContextAccessor { HttpContext = user is null ? null : httpContext };
    }

    private static ClaimsPrincipal CreateUser(string email) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], "Test"));

    [Fact]
    public void AsyncUnaryCall_ShouldAttachOperatorEmail_FromCurrentUser()
    {
        var accessor = CreateAccessor(CreateUser("operator@example.com"));
        var interceptor = new OperatorSubjectInterceptor(accessor);
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
        var entry = Assert.Single(capturedHeaders!, h => h.Key == "x-operator-subject");
        Assert.Equal("operator@example.com", entry.Value);

        call.Dispose();
    }

    [Fact]
    public void AsyncUnaryCall_ShouldSendUnknown_WhenNoHttpContext()
    {
        var accessor = CreateAccessor(user: null);
        var interceptor = new OperatorSubjectInterceptor(accessor);
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
        var entry = Assert.Single(capturedHeaders!, h => h.Key == "x-operator-subject");
        Assert.Equal("unknown", entry.Value);

        call.Dispose();
    }

    [Fact]
    public void AsyncUnaryCall_ShouldPreserveExistingHeaders()
    {
        var accessor = CreateAccessor(CreateUser("operator@example.com"));
        var interceptor = new OperatorSubjectInterceptor(accessor);
        var existingHeaders = new Metadata { { "x-internal-api-key", "shared-secret" } };
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
        Assert.Contains(capturedHeaders!, h => h.Key == "x-internal-api-key" && h.Value == "shared-secret");
        Assert.Contains(capturedHeaders!, h => h.Key == "x-operator-subject" && h.Value == "operator@example.com");

        call.Dispose();
    }
}
