namespace Gateway.Api.Contracts.Shipping;

public sealed record ShippingWebhookEnvelope(
    string? Id,
    string? Type,
    string? Carrier,
    ShippingWebhookData? Data);

public sealed record ShippingWebhookData(
    string? OrderId,
    string? ShipmentId,
    string? TrackingNumber,
    string? ReturnShipmentId,
    string? ReturnTrackingNumber,
    string? CustomerId,
    string? Status);
