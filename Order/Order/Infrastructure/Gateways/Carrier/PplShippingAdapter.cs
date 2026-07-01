using System.Net;
using System.Text.Json;
using Application.DTOs;
using Application.DTOs.ShipmentGateway;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Gateways.Carrier;

public sealed class PplShippingAdapter : ICarrierAdapter, IPplBookingPoller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<PplShippingAdapter> _logger;
    private readonly int _maxPolls;
    private readonly TimeSpan _pollInterval;

    public PplShippingAdapter(
        HttpClient httpClient,
        IOptions<PplApiOptions> options,
        ILogger<PplShippingAdapter> logger)
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

        _maxPolls = opt.MaxPolls > 0 ? opt.MaxPolls : 10;
        _pollInterval = TimeSpan.FromMilliseconds(opt.PollIntervalMs > 0 ? opt.PollIntervalMs : 500);
    }

    public async Task<ShipmentResultDto> CreateShipmentAsync(
        Guid orderId,
        AddressDto deliveryAddress,
        IReadOnlyCollection<OrderItemDto> items,
        CancellationToken cancellationToken)
    {
        var request = new CreateShipmentRequest(
            OrderId: orderId,
            Address: new AddressPayload(
                deliveryAddress.Street,
                deliveryAddress.City,
                deliveryAddress.Country,
                deliveryAddress.PostalCode),
            Packages: items.Select(i => new PackagePayload(i.ProductId, i.Quantity)).ToList()
        );

        using var response = await _http.PostAsJsonAsync(
            "api/v1/parcels",
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new InvalidAddressException($"PPL rejected address/payload. Details: {err}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL create shipment failed. Status={(int)response.StatusCode}, Body={err}");
        }

        var booking = await response.Content.ReadFromJsonAsync<CreateBookingResponse>(JsonOptions, cancellationToken)
                      ?? throw new InvalidOperationException("PPL create shipment response is empty.");

        if (string.IsNullOrWhiteSpace(booking.ReferenceId))
            throw new InvalidOperationException("PPL booking response did not contain a referenceId.");

        // PPL booking is two-phase: the depot accepts asynchronously. Poll the booking
        // reference until it settles as accepted (-> parcel ids) or rejected (-> invalid
        // address), or give up once the polling budget is exhausted.
        for (var attempt = 0; attempt < _maxPolls; attempt++)
        {
            await Task.Delay(_pollInterval, cancellationToken);

            using var pollResponse = await _http.GetAsync(
                $"api/v1/parcels/{booking.ReferenceId}",
                cancellationToken);

            if (!pollResponse.IsSuccessStatusCode)
            {
                var pollErr = await SafeReadAsync(pollResponse, cancellationToken);
                throw new HttpRequestException(
                    $"PPL booking poll failed. ReferenceId={booking.ReferenceId}, Status={(int)pollResponse.StatusCode}, Body={pollErr}");
            }

            var poll = await pollResponse.Content.ReadFromJsonAsync<PollBookingResponse>(JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("PPL booking poll response is empty.");

            switch (poll.Status?.ToLowerInvariant())
            {
                case "accepted":
                    _logger.LogInformation(
                        "PPL booking accepted. OrderId={OrderId}, ParcelId={ParcelId}, TrackingNumber={TrackingNumber}",
                        orderId, poll.ParcelId, poll.TrackingNumber);
                    return new ShipmentResultDto(
                        poll.ParcelId ?? throw new InvalidOperationException("PPL accepted booking is missing parcelId."),
                        poll.TrackingNumber ?? throw new InvalidOperationException("PPL accepted booking is missing trackingNumber."));

                case "rejected":
                    throw new InvalidAddressException(
                        $"PPL rejected booking {booking.ReferenceId}. Reason: {poll.Reason ?? "unspecified"}");

                // "pending" -> keep polling
            }
        }

        throw new PplBookingPendingException(booking.ReferenceId, orderId);
    }

    public async Task CancelShipmentAsync(string shipmentId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            $"api/v1/parcels/{shipmentId}/cancel",
            content: null,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("PPL CancelShipment: shipment not found (idempotent). ShipmentId={ShipmentId}", shipmentId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL cancel shipment failed. ShipmentId={shipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation("PPL shipment cancelled. ShipmentId={ShipmentId}", shipmentId);
    }

    public async Task<ShipmentStatusDto> GetShipmentStatusAsync(
        string trackingNumber,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"api/v1/parcels/tracking/{trackingNumber}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"PPL tracking number not found: {trackingNumber}");

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL get shipment status failed. TrackingNumber={trackingNumber}, Status={(int)response.StatusCode}, Body={err}");
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ShipmentStatusResponse>(JsonOptions, cancellationToken)
                          ?? throw new InvalidOperationException("PPL shipment status response is empty.");

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
            "api/v1/webhooks",
            request,
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL webhook registration failed. ShipmentId={shipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation(
            "PPL webhook registered. ShipmentId={ShipmentId}, Events={Events}",
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
            Packages: items.Select(i => new PackagePayload(i.ProductId, i.Quantity)).ToList());

        using var response = await _http.PostAsJsonAsync(
            "api/v1/parcels/returns",
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new InvalidAddressException($"PPL rejected return shipment. Details: {err}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL create return shipment failed. OrderId={orderId}, Status={(int)response.StatusCode}, Body={err}");
        }

        var dto = await response.Content.ReadFromJsonAsync<CreateReturnShipmentResponse>(JsonOptions, cancellationToken)
                  ?? throw new InvalidOperationException("PPL create return shipment response is empty.");

        _logger.LogInformation(
            "PPL return shipment created. OrderId={OrderId}, ReturnShipmentId={ReturnShipmentId}",
            orderId, dto.ReturnShipmentId);

        return new ReturnShipmentResultDto(dto.ReturnShipmentId, dto.ReturnTrackingNumber, dto.ExpectedPickupDate);
    }

    public async Task CancelReturnShipmentAsync(
        string returnShipmentId,
        string reason,
        CancellationToken cancellationToken)
    {
        var request = new CancelReturnShipmentRequest(Reason: reason);

        using var response = await _http.PostAsJsonAsync(
            $"api/v1/parcels/returns/{returnShipmentId}/cancel",
            request,
            JsonOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "PPL CancelReturnShipment: not found (idempotent). ReturnShipmentId={ReturnShipmentId}",
                returnShipmentId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL cancel return shipment failed. ReturnShipmentId={returnShipmentId}, Status={(int)response.StatusCode}, Body={err}");
        }

        _logger.LogInformation("PPL return shipment cancelled. ReturnShipmentId={ReturnShipmentId}", returnShipmentId);
    }

    public async Task<ReturnShipmentStatusDto> GetReturnShipmentStatusAsync(
        string returnTrackingNumber,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"api/v1/parcels/returns/tracking/{returnTrackingNumber}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"PPL return tracking number not found: {returnTrackingNumber}");

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL get return shipment status failed. ReturnTrackingNumber={returnTrackingNumber}, Status={(int)response.StatusCode}, Body={err}");
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ReturnShipmentStatusResponse>(JsonOptions, cancellationToken)
                          ?? throw new InvalidOperationException("PPL return shipment status response is empty.");

        return new ReturnShipmentStatusDto(
            ReturnTrackingNumber: apiResponse.ReturnTrackingNumber,
            Status: apiResponse.Status,
            PickedUpAt: apiResponse.PickedUpAt,
            DeliveredAt: apiResponse.DeliveredAt);
    }

    public async Task<PplBookingPollResult> PollAsync(string referenceId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"api/v1/parcels/{referenceId}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await SafeReadAsync(response, cancellationToken);
            throw new HttpRequestException(
                $"PPL booking poll failed. ReferenceId={referenceId}, Status={(int)response.StatusCode}, Body={err}");
        }

        var poll = await response.Content.ReadFromJsonAsync<PollBookingResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("PPL booking poll response is empty.");

        return poll.Status?.ToLowerInvariant() switch
        {
            "accepted" => new PplBookingPollResult(PplBookingPollStatus.Accepted, poll.ParcelId, poll.TrackingNumber, null),
            "rejected" => new PplBookingPollResult(PplBookingPollStatus.Rejected, null, null, poll.Reason),
            _ => new PplBookingPollResult(PplBookingPollStatus.Pending, null, null, null),
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return "<unreadable-body>"; }
    }

    // PPL-specific request/response shapes
    private sealed record CreateShipmentRequest(
        Guid OrderId,
        AddressPayload Address,
        IReadOnlyCollection<PackagePayload> Packages);

    private sealed record CreateBookingResponse(string ReferenceId, string Status);

    private sealed record PollBookingResponse(
        string ReferenceId,
        string Status,
        string? ParcelId,
        string? TrackingNumber,
        string? Reason);

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
        IReadOnlyCollection<PackagePayload> Packages);

    private sealed record CreateReturnShipmentResponse(
        string ReturnShipmentId,
        string ReturnTrackingNumber,
        DateTime ExpectedPickupDate);

    private sealed record ReturnShipmentStatusResponse(
        string ReturnTrackingNumber,
        string Status,
        DateTime? PickedUpAt,
        DateTime? DeliveredAt);

    private sealed record CancelReturnShipmentRequest(string Reason);

    private sealed record AddressPayload(string Street, string City, string Country, string PostalCode);
    private sealed record PackagePayload(Guid ProductId, int Quantity);
}
