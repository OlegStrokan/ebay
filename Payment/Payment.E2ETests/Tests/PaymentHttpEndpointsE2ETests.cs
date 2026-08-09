using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Payment.E2ETests.Infrastructure;
using Protos.Common;
using Protos.Payment;
using Xunit;

namespace Payment.E2ETests.Tests;

[Collection("PaymentE2E")]
public sealed class PaymentHttpEndpointsE2ETests : IClassFixture<E2ETestServer>, IAsyncLifetime
{
    private readonly E2ETestServer _server;
    private HttpClient _httpClient = null!;
    private PaymentService.PaymentServiceClient _grpcClient = null!;

    public PaymentHttpEndpointsE2ETests(E2ETestServer server)
    {
        _server = server;
    }

    public Task InitializeAsync()
    {
        _server.ResetAll();
        _httpClient = _server.CreateApiClient();
        _grpcClient = _server.CreatePaymentClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task StripeWebhookEndpoint_ShouldProcessPaymentSucceededEvent()
    {
        var process = await _grpcClient.ProcessPaymentAsync(new ProcessPaymentRequest
        {
            OrderId = $"order-{Guid.NewGuid():N}",
            CustomerId = $"customer-{Guid.NewGuid():N}",
            Amount = ToDecimalValue(20m),
            Currency = "USD",
            PaymentMethod = "card",
            IdempotencyKey = $"pending-{Guid.NewGuid():N}",
        });

        process.Success.Should().BeTrue();
        process.Status.Should().Be(Protos.Payment.ProcessPaymentStatus.Pending);

        var eventId = $"evt-{Guid.NewGuid():N}";
        var payload = $$"""
                        {
                          "id": "{{eventId}}",
                          "type": "payment_intent.succeeded",
                          "data": {
                            "object": {
                              "id": "{{process.ProviderPaymentIntentId}}",
                              "metadata": {
                                "payment_id": "{{process.PaymentId}}"
                              }
                            }
                          }
                        }
                        """;

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/api/v1/webhooks/stripe", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("processed").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("isIgnored").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("providerEventId").GetString().Should().Be(eventId);

        var payment = await _grpcClient.GetPaymentAsync(new GetPaymentRequest { PaymentId = process.PaymentId });
        payment.Success.Should().BeTrue();
        payment.Payment.Status.Should().Be(PaymentRecordStatus.Succeeded);
    }

    [Fact]
    public async Task AdminOrderCallbackEnqueueEndpoint_ShouldQueueCallback()
    {
        var process = await _grpcClient.ProcessPaymentAsync(new ProcessPaymentRequest
        {
            OrderId = $"order-{Guid.NewGuid():N}",
            CustomerId = $"customer-{Guid.NewGuid():N}",
            Amount = ToDecimalValue(45m),
            Currency = "USD",
            PaymentMethod = "card",
            IdempotencyKey = $"success-{Guid.NewGuid():N}",
        });

        process.Success.Should().BeTrue();

        var payload = JsonSerializer.Serialize(new
        {
            paymentId = process.PaymentId,
            callbackType = "PaymentSucceeded",
            refundId = (string?)null,
            errorCode = (string?)null,
            errorMessage = (string?)null,
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/internal/admin/order-callbacks/enqueue")
        {
            Content = content,
        };
        request.Headers.Add("X-Admin-Key", E2ETestServer.AdminApiKey);

        using var response = await _httpClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseJson = await response.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseJson);
        var callbackEventId = responseDoc.RootElement.GetProperty("callbackEventId").GetString();
        callbackEventId.Should().NotBeNullOrWhiteSpace();
        responseDoc.RootElement.GetProperty("paymentId").GetString().Should().Be(process.PaymentId);
        responseDoc.RootElement.GetProperty("callbackType").GetString().Should().Be("PaymentSucceededEvent");

        using var scope = _server.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var callback = await db.OutboundOrderCallbacks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CallbackEventId == callbackEventId);

        callback.Should().NotBeNull();
        callback!.OrderId.Should().NotBeNullOrWhiteSpace();
        callback.EventType.Should().Be("PaymentSucceededEvent");
    }

    [Fact]
    public async Task StripeWebhookEndpoint_ShouldReturnBadRequest_ForEmptyPayload()
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/api/v1/webhooks/stripe", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static DecimalValue ToDecimalValue(decimal value)
    {
        var units = decimal.ToInt64(decimal.Truncate(value));
        var nanos = decimal.ToInt32(decimal.Round((value - units) * 1_000_000_000m));
        return new DecimalValue { Units = units, Nanos = nanos };
    }
}
