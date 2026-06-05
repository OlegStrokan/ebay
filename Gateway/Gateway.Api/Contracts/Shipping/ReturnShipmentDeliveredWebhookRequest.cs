namespace Gateway.Api.Contracts.Shipping;

public sealed record ReturnShipmentDeliveredWebhookRequest(
    string ShipmentId,
    string? TrackingNumber,
    DateTime? DeliveredAt,
    string? OrderId);
