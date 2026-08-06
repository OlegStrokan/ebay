using Api.GrpcServices;
using Api.HealthChecks;
using Api.Middleware;
using Application;
using Infrastructure;
using Infrastructure.ElasticSearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ExceptionHandlingInterceptor>();
});

builder.Services.AddSingleton<ExceptionHandlingInterceptor>();

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder
            .CreateDefault()
            .AddService("search-service"))
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(
                builder.Configuration["Otel:Endpoint"]
                ?? "http://otel-collector:4317");
        }));

builder.Services.AddGrpcHealthChecks(o =>
    {
        o.Services.MapService("", r => r.Tags.Contains("live"));
        o.Services.MapService("ready", r => r.Tags.Contains("ready"));
    })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<ElasticsearchHealthCheck>("elasticsearch", tags: ["ready"]);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider
        .GetRequiredService<ElasticsearchIndexInitializer>();

    await initializer.EnsureIndexAsync();
}

app.MapGrpcService<SearchGrpcService>();
app.MapGrpcHealthChecksService();

app.Run();