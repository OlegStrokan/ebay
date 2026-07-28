using System.Text;
using System.Threading.RateLimiting;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using OpsConsole.Auth;
using OpsConsole.Endpoints;
using OpsConsole.Grpc;
using Protos.AdminOps;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InternalApiKeyInterceptor>();

builder.Services.AddGrpcClient<AdminOpsService.AdminOpsServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["OrderServiceUrl"]
                             ?? "http://localhost:5224");
}).AddInterceptor<InternalApiKeyInterceptor>();

// Phase 6 cross-service correlation: same shared-secret pattern, pointed at
// Payment's and Inventory's own admin gRPC services instead of Order's.
builder.Services.AddGrpcClient<AdminPaymentService.AdminPaymentServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["PaymentServiceUrl"]
                             ?? "http://localhost:5080");
}).AddInterceptor<InternalApiKeyInterceptor>();

builder.Services.AddGrpcClient<AdminInventoryService.AdminInventoryServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["InventoryServiceUrl"]
                             ?? "http://localhost:5074");
}).AddInterceptor<InternalApiKeyInterceptor>();

// JWT auth. Originally (Phase 4) only mutating endpoints required it, with reads
// staying behind ApiKeyMiddleware alone. Phase 7 extends the same JWT + role check to
// read endpoints too ("OpsViewer" policy below) — the shared X-Admin-Api-Key can end
// up copy-pasted into more places than intended, so viewing saga/payment/DLQ data now
// also requires a real operator identity. Tokens are the same ones Auth/Gateway already
// issue (shared Jwt:SecretKey + Jwt:Audience — see Gateway.Api/Program.cs for the same
// pattern).
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
                await context.Response.WriteAsJsonAsync(new { error = "Operator lacks the role required for this action." });
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Mutations: unchanged from Phase 4/5.
    options.AddPolicy("OpsAdmin", policy => policy.RequireRole("Admin", "SuperAdmin"));

    // Phase 7 view-level RBAC: reads now also require a real operator identity, not
    // just the shared X-Admin-Api-Key (which could be checked into a config file and
    // shared by many people). "OpsViewer" is included so a future lower-privileged
    // read-only role can be granted via the existing User-service AssignRole flow
    // without touching this policy again — nobody needs to actually hold that role
    // today since Admin/SuperAdmin already satisfy it.
    options.AddPolicy("OpsViewer", policy => policy.RequireRole("Admin", "SuperAdmin", "OpsViewer"));
});

builder.Services.AddRateLimiter(options =>
{
    // The middleware defaults to 503 on rejection, which would be indistinguishable
    // from Order/Payment/Inventory being genuinely unreachable (also mapped to 503
    // below) — 429 makes "you're being throttled" unambiguous to the caller.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please slow down." }, cancellationToken);
    };

    // Mutating saga/DLQ actions: capped independent of the JWT/API-key checks above,
    // so a compromised token or a scripting mistake can't hammer compensation/requeue
    // endlessly. Partitioned by IP, same approach as Gateway's "auth-strict" policy.
    options.AddPolicy("ops-mutation", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                PermitLimit = 20,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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
app.MapSagaCorrelationEndpoints();

app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
