using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using OpsConsole.Auth;
using OpsConsole.Endpoints;
using OpsConsole.Grpc;
using Protos.AdminOps;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OrderInternalKeyInterceptor>();

builder.Services.AddGrpcClient<AdminOpsService.AdminOpsServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["OrderServiceUrl"]
                             ?? "http://localhost:5224");
}).AddInterceptor<OrderInternalKeyInterceptor>();

var app = builder.Build();

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async httpContext =>
    {
        var feature = httpContext.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is RpcException rpcEx)
        {
            (httpContext.Response.StatusCode, var message) = rpcEx.StatusCode switch
            {
                StatusCode.NotFound        => (StatusCodes.Status404NotFound,           rpcEx.Status.Detail),
                StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest,          rpcEx.Status.Detail),
                // PermissionDenied here means Order rejected OpsConsole's internal API
                // key (misconfiguration), not that the operator lacks access — 502 signals
                // an upstream problem rather than blaming the caller with 403.
                StatusCode.PermissionDenied=> (StatusCodes.Status502BadGateway,          "Upstream service rejected the request."),
                StatusCode.Unavailable     => (StatusCodes.Status503ServiceUnavailable,  "Upstream service unavailable."),
                _                          => (StatusCodes.Status500InternalServerError, "An internal error occurred.")
            };
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsJsonAsync(new { error = message });
        }
    });
});

app.UseMiddleware<ApiKeyMiddleware>();

app.MapSagaEndpoints();

app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
