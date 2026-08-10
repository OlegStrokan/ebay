using Api.GrpcServices;
using Api.HealthChecks;
using Application;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

builder.Services.AddGrpc();

// Liveness (default "" service) = cheap self-check; readiness ("ready" service) = real deps.
// Connection strings resolve lazily (post-build) so test hosts can override them.
builder.Services.AddGrpcHealthChecks(o =>
    {
        o.Services.MapService("", r => r.Tags.Contains("live"));
        o.Services.MapService("ready", r => r.Tags.Contains("ready"));
    })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!,
        name: "postgres", tags: ["ready"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
	.WithTracing(b => b
		.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("inventory-service"))
		.AddAspNetCoreInstrumentation()
		.AddEntityFrameworkCoreInstrumentation()
		.AddOtlpExporter(o =>
			o.Endpoint = new Uri(
				builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
	var initializer = scope.ServiceProvider.GetRequiredService<InventoryDbInitializer>();
	await initializer.MigrateAsync();
}

app.MapGrpcService<InventoryGrpcService>();
app.MapGrpcService<AdminInventoryGrpcService>();
app.MapGrpcHealthChecksService();

app.MapGet("/healthz/live", () => Results.Ok("live"));
app.MapGet("/healthz/ready", () => Results.Ok("ready"));

app.Run();

public partial class Program { }
