using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.HealthChecks;

// Cluster-reachability probe: a plain GET on Elasticsearch's cluster-health endpoint. Read-only,
// no index writes — safe to run on every readiness probe.
public sealed class ElasticsearchHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private static readonly HttpClient Http = new();

    private readonly string _uri =
        configuration["Elasticsearch:Uri"]
        ?? configuration["Elasticsearch:Url"]
        ?? "http://localhost:9200";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync(
                new Uri(new Uri(_uri), "/_cluster/health"), cts.Token);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Elasticsearch returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Elasticsearch is unreachable", ex);
        }
    }
}
