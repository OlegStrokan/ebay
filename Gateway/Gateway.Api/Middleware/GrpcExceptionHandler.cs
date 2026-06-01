using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Api.Middleware;

public sealed class GrpcExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GrpcExceptionHandler> _logger;

    public GrpcExceptionHandler(ILogger<GrpcExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not RpcException rpcException)
            return false;

        _logger.LogWarning(rpcException,
            "gRPC call failed with status {Status}: {Detail}",
            rpcException.StatusCode, rpcException.Status.Detail);

        var (statusCode, title) = rpcException.StatusCode switch
        {
            StatusCode.NotFound => (StatusCodes.Status404NotFound, "Not Found"),
            StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest, "Bad Request"),
            StatusCode.AlreadyExists => (StatusCodes.Status409Conflict, "Conflict"),
            StatusCode.PermissionDenied => (StatusCodes.Status403Forbidden, "Forbidden"),
            StatusCode.Unauthenticated => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            StatusCode.FailedPrecondition => (StatusCodes.Status412PreconditionFailed, "Precondition Failed"),
            StatusCode.ResourceExhausted => (StatusCodes.Status429TooManyRequests, "Too Many Requests"),
            StatusCode.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Service Unavailable"),
            StatusCode.DeadlineExceeded => (StatusCodes.Status504GatewayTimeout, "Gateway Timeout"),
            _ => (StatusCodes.Status502BadGateway, "Bad Gateway")
        };

        // Only surface the gRPC detail for explicit client-error codes where the backend
        // sets a user-facing message. Infrastructure / server-side codes could leak
        // internal topology, stack traces, or service names to callers.
        var safeDetail = rpcException.StatusCode switch
        {
            StatusCode.NotFound or
            StatusCode.InvalidArgument or
            StatusCode.AlreadyExists or
            StatusCode.PermissionDenied or
            StatusCode.Unauthenticated or
            StatusCode.FailedPrecondition or
            StatusCode.ResourceExhausted => rpcException.Status.Detail,
            _ => null
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = safeDetail,
        }, cancellationToken);

        return true;
    }
}
