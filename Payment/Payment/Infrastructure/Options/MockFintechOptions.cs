namespace Infrastructure.Options;

public sealed class MockFintechOptions
{
    public const string SectionName = "MockFintech";

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 10;
}
