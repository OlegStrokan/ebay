using Api.GrpcServices;
using Application;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

builder.Services.AddGrpc();

//@todo: add real health checks
builder.Services.AddGrpcHealthChecks()
	.AddCheck("Sample", () => HealthCheckResult.Healthy());

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
	await initializer.EnsureCreatedAsync();
}

app.MapGrpcService<InventoryGrpcService>();
app.MapGrpcHealthChecksService();

app.MapGet("/healthz/live", () => Results.Ok("live"));
app.MapGet("/healthz/ready", () => Results.Ok("ready"));

app.Run();

public partial class Program { }
