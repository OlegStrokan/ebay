using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Api.Middleware;

public sealed class ApiKeyAuthInterceptor(
    IConfiguration configuration,
    ILogger<ApiKeyAuthInterceptor> logger) : Interceptor
{
    private const string ApiKeyHeader = "x-internal-api-key";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var expectedKey = configuration["InternalServices:AccountingApiKey"];

        if (string.IsNullOrEmpty(expectedKey))
        {
            logger.LogError("InternalServices:AccountingApiKey is not configured; rejecting call.");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }

        var providedKey = context.RequestHeaders.GetValue(ApiKeyHeader) ?? string.Empty;

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedKey),
                Encoding.UTF8.GetBytes(expectedKey)))
        {
            logger.LogWarning("Rejected gRPC call with missing/invalid {Header}.", ApiKeyHeader);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Caller not authorized."));
        }

        return await continuation(request, context);
    }
}
