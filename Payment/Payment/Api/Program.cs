using Api.Endpoints;
using Api.GrpcServices;
using Api.HealthChecks;
using Api.Middleware;
using Application;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("test"))
{
    RequireDeployedSecret("InternalServices:OpsConsoleApiKey");
}

// fail loudly if someone deploy prod with placeholder
void RequireDeployedSecret(string key)
{
    var value = builder.Configuration[key];

    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"{key} must be configured outside development.");

    string[] placeholderPrefixes = ["dev_", "replace-me", "change-me"];

    if (placeholderPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException(
            $"{key} is still a placeholder. Rotate it before deploying outside development.");
}

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ExceptionHandlingInterceptor>();
});

builder.Services.AddSingleton<ExceptionHandlingInterceptor>();

var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

// Liveness (default "" service) = cheap self-check; readiness ("ready" service) = real deps.
builder.Services.AddGrpcHealthChecks(o =>
    {
        o.Services.MapService("", r => r.Tags.Contains("live"));
        o.Services.MapService("ready", r => r.Tags.Contains("ready"));
    })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(postgresConnection, name: "postgres", tags: ["ready"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DbContext.PaymentDbContext>();
    await db.Database.MigrateAsync();
}

app.MapGrpcService<PaymentGrpcService>();
app.MapGrpcService<AdminPaymentGrpcService>();
app.MapGrpcHealthChecksService();

app.MapStripeWebhookEndpoint();
app.MapAdminOrderCallbackEndpoint();

app.Run();

public partial class Program;