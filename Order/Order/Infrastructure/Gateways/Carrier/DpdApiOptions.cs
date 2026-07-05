namespace Infrastructure.Gateways.Carrier;

public sealed class DpdApiOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 20;

    // When set, the adapter adds X-Carrier-Test-Scenario: <value> to every request so
    // the fake DPD service can activate magic-token behavior ("slow", "lost", "fail", …)
    // that is unreachable via a plain Guid orderId. Leave empty in production.
    public string TestScenario { get; init; } = string.Empty;
}
