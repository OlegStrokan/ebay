using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Api.Middleware;

public sealed class ExceptionHandlingInterceptor(
    ILogger<ExceptionHandlingInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
    }

    private RpcException TranslateException(Exception ex)
    {
        if (ex is RpcException rpc)
            return rpc;

        if (ex is ArgumentException)
        {
            logger.LogWarning(ex, "Validation error in gRPC call.");
            return new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        logger.LogError(ex, "Unhandled exception in gRPC call.");
        return new RpcException(new Status(StatusCode.Internal, "An internal error occurred."));
    }
}
