using System.Net;
using System.Text.Json;
using Application.DTOs;
using Application.DTOs.ShipmentGateway;
using Application.Gateways.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Gateways.Carrier;

public sealed class DpdShippingAdapter : ICarrierAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<DpdShippingAdapter> _logger;

    public DpdShippingAdapter(
        HttpClient httpClient,
        IOptions<DpdApiOptions> options,
        ILogger<DpdShippingAdapter> logger)
    {
        _http = httpClient;
        _logger = logger;
        var opt = options.Value;

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(opt.BaseUrl))
            _http.BaseAddress = new Uri(opt.BaseUrl);

        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {opt.ApiKey}");
        }

        if (!string.IsNullOrWhiteSpace(opt.TestScenario))
        {
            _http.DefaultRequestHeaders.Remove("X-Carrier-Test-Scenario");
            _http.DefaultRequestHeaders.Add("X-Carrier-Test-Scenario", opt.TestScenario);
        }

        if (opt.TimeoutSeconds > 0)
            _http.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
    }

    public async Task<ShipmentResultDto> CreateShipmentAsync(
        Guid orderId,
        AddressDto deliveryAddress,
        IReadOnlyCollection<OrderItemDto> items,
        CancellationToken cancellationToken)
    {
        var request = new CreateShipmentRequest(
            OrderId: orderId,
            Recipient: new RecipientPayload(
                deliveryAddress.Street,
                deliveryAddress.City,
                deliveryAddress.Country,
                deliveryAddress.PostalCode),
            Parcels: items.Select(i => new ParcelPayload(i.ProductId, i.Quantity)).ToList()
        );

        using var response = await _http.PostAsJsonAsync(
            "v1/shipment",
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new InvalidAddressException($"DPD rejected address/payload. Details: {err}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD create shipment failed. Status={(int)response.StatusCode}, Body={err}");
        }

        var dto = await response.Content.ReadFromJsonAsync<CreateShipmentResponse>(JsonOptions, cancellationToken)
                  ?? throw new InvalidOperationException("DPD create shipment response is empty.");

        _logger.LogInformation(
            "DPD shipment created. OrderId={OrderId}, ShipmentId={ShipmentId}, TrackingNumber={TrackingNumber}",
            orderId, dto.ShipmentId, dto.TrackingNumber);

        return new ShipmentResultDto(dto.ShipmentId, dto.TrackingNumber);
    }

    public async Task CancelShipmentAsync(string shipmentId, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(
            $"v1/shipment/{shipmentId}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("DPD CancelShipment: shipment not found (idempotent). ShipmentId={ShipmentId}", shipmentId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD cancel shipment failed. ShipmentId={shipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation("DPD shipment cancelled. ShipmentId={ShipmentId}", shipmentId);
    }

    public async Task<ShipmentStatusDto> GetShipmentStatusAsync(
        string trackingNumber,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"v1/shipment/{trackingNumber}/status",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"DPD tracking number not found: {trackingNumber}");

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD get shipment status failed. TrackingNumber={trackingNumber}, Status={(int)response.StatusCode}, Body={err}");
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ShipmentStatusResponse>(JsonOptions, cancellationToken)
                          ?? throw new InvalidOperationException("DPD shipment status response is empty.");

        return new ShipmentStatusDto(
            TrackingNumber: apiResponse.TrackingNumber,
            Status: apiResponse.Status,
            EstimatedDeliveryDate: apiResponse.EstimatedDeliveryDate,
            ActualDeliveryDate: apiResponse.ActualDeliveryDate,
            CurrentLocation: apiResponse.CurrentLocation);
    }

    public async Task RegisterWebhookAsync(
        string shipmentId,
        string callbackUrl,
        string[] events,
        CancellationToken cancellationToken)
    {
        var request = new RegisterWebhookRequest(
            ShipmentId: shipmentId,
            CallbackUrl: callbackUrl,
            Events: events);

        using var response = await _http.PostAsJsonAsync(
            "v1/webhook",
            request,
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD webhook registration failed. ShipmentId={shipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation(
            "DPD webhook registered. ShipmentId={ShipmentId}, Events={Events}",
            shipmentId, string.Join(", ", events));
    }

    public async Task<ReturnShipmentResultDto> CreateReturnShipmentAsync(
        Guid orderId,
        Guid customerId,
        List<OrderItemDto> items,
        CancellationToken cancellationToken)
    {
        var request = new CreateReturnShipmentRequest(
            OrderId: orderId,
            CustomerId: customerId,
            Parcels: items.Select(i => new ParcelPayload(i.ProductId, i.Quantity)).ToList());

        using var response = await _http.PostAsJsonAsync(
            "v1/shipment/return",
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new InvalidAddressException($"DPD rejected return shipment. Details: {err}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD create return shipment failed. OrderId={orderId}, Status={(int)response.StatusCode}, Body={err}");
        }

        var dto = await response.Content.ReadFromJsonAsync<CreateReturnShipmentResponse>(JsonOptions, cancellationToken)
                  ?? throw new InvalidOperationException("DPD create return shipment response is empty.");

        _logger.LogInformation(
            "DPD return shipment created. OrderId={OrderId}, ReturnShipmentId={ReturnShipmentId}",
            orderId, dto.ReturnShipmentId);

        return new ReturnShipmentResultDto(dto.ReturnShipmentId, dto.ReturnTrackingNumber, dto.ExpectedPickupDate);
    }

    public async Task CancelReturnShipmentAsync(
        string returnShipmentId,
        string reason,
        CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(
            $"v1/shipment/return/{returnShipmentId}?reason={Uri.EscapeDataString(reason)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "DPD CancelReturnShipment: not found (idempotent). ReturnShipmentId={ReturnShipmentId}",
                returnShipmentId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD cancel return shipment failed. ReturnShipmentId={returnShipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation("DPD return shipment cancelled. ReturnShipmentId={ReturnShipmentId}", returnShipmentId);
    }

    public async Task<ReturnShipmentStatusDto> GetReturnShipmentStatusAsync(
        string returnTrackingNumber,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"v1/shipment/return/{returnTrackingNumber}/status",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"DPD return tracking number not found: {returnTrackingNumber}");

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"DPD get return shipment status failed. ReturnTrackingNumber={returnTrackingNumber}, Status={(int)response.StatusCode}, Body={err}");
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ReturnShipmentStatusResponse>(JsonOptions, cancellationToken)
                          ?? throw new InvalidOperationException("DPD return shipment status response is empty.");

        return new ReturnShipmentStatusDto(
            ReturnTrackingNumber: apiResponse.ReturnTrackingNumber,
            Status: apiResponse.Status,
            PickedUpAt: apiResponse.PickedUpAt,
            DeliveredAt: apiResponse.DeliveredAt);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return "<unreadable-body>"; }
    }

    // DPD-specific request/response shapes
    private sealed record CreateShipmentRequest(
        Guid OrderId,
        RecipientPayload Recipient,
        IReadOnlyCollection<ParcelPayload> Parcels);

    private sealed record CreateShipmentResponse(string ShipmentId, string TrackingNumber);

    private sealed record ShipmentStatusResponse(
        string TrackingNumber,
        string Status,
        DateTime? EstimatedDeliveryDate,
        DateTime? ActualDeliveryDate,
        string? CurrentLocation);

    private sealed record RegisterWebhookRequest(string ShipmentId, string CallbackUrl, string[] Events);

    private sealed record CreateReturnShipmentRequest(
        Guid OrderId,
        Guid CustomerId,
        IReadOnlyCollection<ParcelPayload> Parcels);

    private sealed record CreateReturnShipmentResponse(
        string ReturnShipmentId,
        string ReturnTrackingNumber,
        DateTime ExpectedPickupDate);

    private sealed record ReturnShipmentStatusResponse(
        string ReturnTrackingNumber,
        string Status,
        DateTime? PickedUpAt,
        DateTime? DeliveredAt);

    private sealed record RecipientPayload(string Street, string City, string Country, string PostalCode);
    private sealed record ParcelPayload(Guid ProductId, int Quantity);
}
