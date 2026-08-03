namespace Domain.ValueObjects;

public sealed record Money
{
    public decimal Amount { get; }

    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));

        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }
}
