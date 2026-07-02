using Api.GrpcServices;
using Application;
using FluentValidation;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Protos.Order;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// Scan Api assembly for validators. ApplicationModule already scans the Application assembly,
// so together they cover validators in both layers.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddGrpc();

builder.Services.AddGrpcHealthChecks()
    .AddCheck("Sample", () => HealthCheckResult.Healthy());


builder.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("order-service"))
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("OrderService.Kafka")
        .AddOtlpExporter(o =>
            o.Endpoint = new Uri(
                builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var writeDb = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DbContext.AppDbContext>();
    await writeDb.Database.MigrateAsync();
    var readDb = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DbContext.ReadDbContext>();
    await readDb.Database.MigrateAsync();
}

app.MapGrpcService<OrderGrpcService>();
app.MapGrpcService<B2BOrderGrpcService>();
app.MapGrpcService<RecurringOrderGrpcService>();
app.MapGrpcHealthChecksService();


app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
