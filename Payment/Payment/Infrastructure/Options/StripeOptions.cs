namespace Infrastructure.Options;

public enum PaymentProviderType
{
    Stripe,      // Real Stripe — production
    MockFintech, // Custom fintech REST API — staging / partner testing
    Fake         // In-memory simulation — unit and integration tests only
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    // No default. Missing or invalid value causes startup validation to throw.
    public PaymentProviderType ProviderType { get; init; }

    public string SecretKey { get; init; } = string.Empty;

    public string WebhookSecret { get; init; } = string.Empty;

    public int WebhookToleranceSeconds { get; init; } = 300;

    public string DefaultCurrency { get; init; } = "USD";
}