using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using ProductAdmin.Auth;
using ProductAdmin.Endpoints;
using Protos.Product;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpcClient<ProductService.ProductServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["ProductServiceUrl"]
                             ?? "http://localhost:5050");
});

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
                StatusCode.NotFound        => (StatusCodes.Status404NotFound,            rpcEx.Status.Detail),
                StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest,          rpcEx.Status.Detail),
                StatusCode.AlreadyExists   => (StatusCodes.Status409Conflict,            rpcEx.Status.Detail),
                StatusCode.Unauthenticated => (StatusCodes.Status401Unauthorized,        rpcEx.Status.Detail),
                StatusCode.PermissionDenied=> (StatusCodes.Status403Forbidden,           rpcEx.Status.Detail),
                _                          => (StatusCodes.Status500InternalServerError, "An internal error occurred.")
            };
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsJsonAsync(new { error = message });
        }
    });
});

app.UseMiddleware<ApiKeyMiddleware>();

app.MapProductModerationEndpoints();
app.MapProductAdminEndpoints();
app.MapCatalogItemEndpoints();

app.Run();
