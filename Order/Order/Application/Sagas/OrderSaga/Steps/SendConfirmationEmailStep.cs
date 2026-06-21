using System.Text.Json;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.Steps;
using Microsoft.Extensions.Logging;

namespace Application.Sagas.OrderSaga.Steps;

public sealed class SendConfirmationEmailStep(
        IEmailGateway _emailGateway,
        IDeadLetterRepository _deadLetterRepository,
        ILogger<SendConfirmationEmailStep> _logger
    ) : ISagaStep<OrderSagaData, OrderSagaContext>
{
    private const int MaxEmailAttempts = 3;

    public string StepName => "SendConfirmationEmail";
    public int Order => 8;


    public async Task<StepOutcome> ExecuteAsync(
        OrderSagaData data,
        OrderSagaContext context,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation(
            "Sending confirmation email for order {OrderId} to customer {CustomerId}",
            data.CorrelationId,
            data.CustomerId);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxEmailAttempts; attempt++)
        {
            try
            {
                await _emailGateway.SendOrderConfirmationAsync(
                    customerId: data.CustomerId,
                    orderId: data.CorrelationId,
                    orderTotal: data.TotalAmount,
                    currency: data.Currency,
                    items: data.Items,
                    deliveryDelivery: data.DeliveryAddress,
                    estimatedDelivery: DateTime.UtcNow.AddDays(5),
                    cancellationToken);

                return new Completed(new Dictionary<string, object>
                {
                    ["EmailSent"] = true,
                    ["CustomerId"] = data.CustomerId
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Email send attempt {Attempt}/{Max} failed for order {OrderId}",
                    attempt, MaxEmailAttempts, data.CorrelationId);

                if (attempt < MaxEmailAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }

        // Email failure does NOT fail the order; the customer can be notified through
        // other channels (support ticket, admin re-send type shit)
        _logger.LogCritical(
            lastException,
            "Confirmation email permanently failed for order {OrderId} after {Max} attempts. Writing to dead-letter.",
            data.CorrelationId, MaxEmailAttempts);

        var deadLetterContent = JsonSerializer.Serialize(new
        {
            OrderId = data.CorrelationId,
            CustomerId = data.CustomerId,
            TotalAmount = data.TotalAmount,
            Currency = data.Currency,
            FailureReason = lastException?.Message
        });

        await _deadLetterRepository.AddAsync(
            messageId: Guid.NewGuid(),
            type: "EmailConfirmationFailed",
            content: deadLetterContent,
            occuredOn: DateTime.UtcNow,
            failureReason: lastException?.Message ?? "Unknown",
            retryCount: MaxEmailAttempts,
            aggregateId: data.CorrelationId.ToString(),
            ct: cancellationToken);

        return new Completed(new Dictionary<string, object>
        {
            ["EmailSent"] = false,
            ["Warning"] = "Email failed after all retries — written to dead-letter queue"
        });
    }

    public async Task CompensateAsync(OrderSagaData data, OrderSagaContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "No compensation needed for confirmation email step (order {OrderId})",
            data.CorrelationId);

        await Task.CompletedTask;
    }
}