using System.Threading.RateLimiting;
using Gateway.Api.Endpoints;
using Gateway.Api.Extensions;
using Gateway.Api.Middleware;
using Gateway.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
var jwtAuthority = builder.Configuration["Jwt:Authority"];
var jwtAudience = builder.Configuration["Jwt:Audience"];
var jwtSecretKey = builder.Configuration["Jwt:SecretKey"];

if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtAudience))
{
    throw new InvalidOperationException("JWT audience must be configured outside development.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (string.IsNullOrWhiteSpace(jwtSecretKey))
            options.Authority = jwtAuthority;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidAudience = jwtAudience,
            ValidateIssuer = !builder.Environment.IsDevelopment(),
            IssuerSigningKey = string.IsNullOrWhiteSpace(jwtSecretKey) ? null :
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(jwtSecretKey))
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { success = false, message = "Unauthorized. A valid access token is required." });
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireRole("Admin", "SuperAdmin"));
});

builder.Services.AddRateLimiter(options =>
{
    // 5 attempts per minute per IP — login and password-reset
    options.AddPolicy("auth-strict", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                PermitLimit = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));

    // 30 requests per 10 seconds per IP — search
    options.AddPolicy("search", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                PermitLimit = 30,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddGrpcClients(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Free eBay Gateway API",
        Version = "v1"
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddExceptionHandler<GrpcExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddSingleton<IUserEventPublisher, KafkaUserEventPublisher>();
builder.Services.AddSingleton<IOrderSagaEventPublisher, KafkaOrderSagaEventPublisher>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapProductEndpoints();
app.MapListingEndpoints();
app.MapOrderEndpoints();
app.MapB2BOrderEndpoints();
app.MapRecurringOrderEndpoints();
app.MapPaymentEndpoints();
app.MapInventoryEndpoints();
app.MapSearchEndpoints();
app.MapUserEventEndpoints();
app.MapShippingWebhookEndpoints();

app.Run();

// Make the implicit Program class public so test projects can reference it
public partial class Program { }
