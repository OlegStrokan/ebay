using Email.HealthChecks;
using Email.Messaging;
using Email.Options;
using Email.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<EmailDeliveryOptions>(builder.Configuration.GetSection(EmailDeliveryOptions.SectionName));

builder.Services.AddSingleton<IProcessedMessageStore, PostgresProcessedMessageStore>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<KafkaEmailConsumer>();
builder.Services.AddHostedService<KafkaEmailDlqReplayWorker>();
builder.Services.AddHostedService<ProcessedMessageCleanupWorker>();

// This worker has no gRPC surface, so health is exposed over plain HTTP for the kubelet probes.
var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=email_service";

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(postgresConnection, name: "postgres", tags: ["ready"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.Run();
