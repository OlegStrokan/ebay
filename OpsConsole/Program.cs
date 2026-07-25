using System.Text;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
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

// JWT auth for mutating endpoints only — read endpoints stay behind ApiKeyMiddleware
// alone (unchanged). Mutations additionally require a real operator identity + role,
// so audit log entries can record "who", and so a leaked/shared admin API key alone
// can't trigger saga compensation. Tokens are the same ones Auth/Gateway already issue
// (shared Jwt:SecretKey + Jwt:Audience — see Gateway.Api/Program.cs for the same pattern).
var jwtSecretKey = builder.Configuration["Jwt:SecretKey"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtSecretKey))
{
    throw new InvalidOperationException("Jwt:SecretKey must be configured outside development.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidAudience = jwtAudience,
            ValidateIssuer = false,
            IssuerSigningKey = string.IsNullOrWhiteSpace(jwtSecretKey)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecretKey))
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = "A valid operator access token is required." });
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { error = "Operator lacks the Admin/SuperAdmin role required for this action." });
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OpsAdmin", policy => policy.RequireRole("Admin", "SuperAdmin"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

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
app.MapDeadLetterEndpoints();
app.MapSagaMutationEndpoints();
app.MapDeadLetterMutationEndpoints();

app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
