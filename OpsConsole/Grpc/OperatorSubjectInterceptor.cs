using System.Security.Claims;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OpsConsole.Grpc;

// Forwards the calling operator's identity to Order/Payment/Inventory's admin gRPC services
public class OperatorSubjectInterceptor(IHttpContextAccessor httpContextAccessor) : Interceptor
{
    private const string Header = "x-operator-subject";

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

        headers.Add(Header, GetOperatorSubject(httpContextAccessor.HttpContext?.User));

        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);

        return continuation(request, newContext);
    }

    private static string GetOperatorSubject(ClaimsPrincipal? user) =>
        user?.FindFirstValue(ClaimTypes.Email)
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
