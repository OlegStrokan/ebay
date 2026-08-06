using Api.GrpcServices;
using Api.HealthChecks;
using Application;
using FluentValidation;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Protos.Order;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// Scan Api assembly for validators. ApplicationModule already scans the Application assembly,
// so together they cover validators in both layers.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddGrpc();

builder.Services.AddGrpcHealthChecks(o =>
    {
        o.Services.MapService("", r => r.Tags.Contains("live"));
        o.Services.MapService("ready", r => r.Tags.Contains("ready"));
    })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!,
        name: "postgres-write", tags: ["ready"])
    .AddNpgSql(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("PostgresReadModel")
            ?? sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!,
        name: "postgres-read", tags: ["ready"])
    .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), name: "redis", tags: ["ready"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);


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
app.MapGrpcService<AdminOpsGrpcService>();
app.MapGrpcHealthChecksService();


app.Run();

// Make Program accessible to WebApplicationFactory in test projects
public partial class Program { }
