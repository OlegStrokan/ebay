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
        catch (RpcException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Validation error in accounting gRPC call");
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in accounting gRPC call");
            throw new RpcException(new Status(StatusCode.Internal, "An internal error occurred"));
        }
    }
}
