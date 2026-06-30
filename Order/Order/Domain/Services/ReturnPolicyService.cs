namespace Domain.Services;

public record ReturnPolicyContext(
    string CountryCode,
    List<string> ProductCategories,
    string CustomerTier,
    bool IsHolidaySeason
);


public class ReturnPolicyService
{
    private static readonly HashSet<string> EuCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    };

    public TimeSpan CalculateReturnWindow(ReturnPolicyContext context)
    {
        var window = TimeSpan.FromDays(7);

        // EU Directive 2011/83/EU mandates 14 days; this platform extends to 30 for
        // better customer experience and to avoid per-country edge-case tracking.
        if (EuCountries.Contains(context.CountryCode))
            window = Max(window, TimeSpan.FromDays(30));

        // Subscriber tier adds 7 days on top of whatever the base/EU window already is,
        // so the benefit is always meaningful (including for EU customers at 30 days).
        if (context.CustomerTier == "Subscriber")
            window = Add(window, TimeSpan.FromDays(7));

        if (context.CustomerTier == "Premium")
            window = Add(window, TimeSpan.FromDays(30));

        if (context.IsHolidaySeason)
            window = Add(window, TimeSpan.FromDays(14));

        return window;
    }

    public static bool IsHolidaySeason(DateTime utcNow)
    {
        var month = utcNow.Month;
        var day = utcNow.Day;
        // Holiday season: November 15 – January 15
        return month == 12 || (month == 11 && day >= 15) || (month == 1 && day <= 15);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Add(TimeSpan base_, TimeSpan extra) => base_ + extra;
}
