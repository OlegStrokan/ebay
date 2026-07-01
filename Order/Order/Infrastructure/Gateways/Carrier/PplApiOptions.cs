namespace Infrastructure.Gateways.Carrier;

public sealed class PplApiOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 20;

    // PPL booking is two-phase: after the initial 202 the adapter polls the booking
    // reference until the depot accepts or rejects it.
    public int MaxPolls { get; init; } = 10;
    public int PollIntervalMs { get; init; } = 500;

    // When set, the adapter adds X-Carrier-Test-Scenario: <value> to every request so
    // the fake PPL service can activate magic-token behavior ("slowpoll", "pollreject",
    // "cancelblock", …) that is unreachable via a plain Guid orderId. Leave empty in production.
    public string TestScenario { get; init; } = string.Empty;
}
