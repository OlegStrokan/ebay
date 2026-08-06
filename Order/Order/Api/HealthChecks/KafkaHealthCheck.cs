using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.HealthChecks;

// Broker-reachability probe via an admin metadata request. Deliberately does NOT produce a
// message: readiness runs on every probe and must not write to Kafka as a side effect
public sealed class KafkaHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private readonly string _bootstrapServers =
        configuration["Kafka:BootstrapServers"] ?? "localhost:9092";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var adminClient = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = _bootstrapServers }).Build();
            adminClient.GetMetadata(TimeSpan.FromSeconds(5));
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Kafka broker is unreachable", ex));
        }
    }
}
