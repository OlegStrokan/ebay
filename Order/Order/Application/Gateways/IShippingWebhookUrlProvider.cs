namespace Application.Gateways;

public interface IShippingWebhookUrlProvider
{
    string GetReturnDeliveredCallbackUrl();
}
