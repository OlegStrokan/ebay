using Api.GrpcServices;
using Application;
using FluentValidation;
using Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// Validators in the Api assembly (request-level validators for gRPC methods)
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddGrpc();
builder.Services.AddScoped<ProductGrpcHandler>();
builder.Services.AddScoped<ListingGrpcHandler>();

builder.Services.AddGrpcHealthChecks()
    .AddCheck("Sample", () => HealthCheckResult.Healthy());

builder.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("product-service"))
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("ProductService.Kafka")
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(
                builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DbContext.ProductDbContext>();
    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

app.MapGrpcService<ProductGrpcService>();
app.MapGrpcHealthChecksService();

app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
