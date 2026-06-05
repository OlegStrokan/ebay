using Application.Gateways;
using Microsoft.Extensions.Options;

namespace Infrastructure.Gateways;

public sealed class ShippingWebhookUrlProvider(
    IOptions<ShippingApiOptions> options) : IShippingWebhookUrlProvider
{
    private readonly ShippingApiOptions _options = options.Value;

    public string GetReturnDeliveredCallbackUrl()
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookCallUrl))
        {
            throw new InvalidOperationException(
                "Shipping webhook callback URL is not configured. Set Shipping:WebhookCallUrl.");
        }

        return _options.WebhookCallUrl.Trim();
    }
}
