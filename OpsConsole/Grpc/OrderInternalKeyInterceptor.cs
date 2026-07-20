using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OpsConsole.Grpc;

public class OrderInternalKeyInterceptor(IConfiguration config) : Interceptor
{
    private const string Header = "x-internal-api-key";

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var headers = new Metadata();

        if (context.Options.Headers is not null)
        {
            foreach (var entry in context.Options.Headers)
                headers.Add(entry);
        }

        headers.Add(Header, config["InternalServices:OpsConsoleApiKey"] ?? string.Empty);

        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);

        return continuation(request, newContext);
    }
}
