using Application.Commands.EnqueueOrderCallback;
using Application.Common;
using Application.Commands.HandleStripeWebhook;
using Application.Commands.ProcessPayment;
using Application.Commands.ReconcilePendingPayments;
using Application.Commands.RefundPayment;
using Application.Commands.CapturePayment;
using Application.Queries.GetPaymentById;
using Application.Queries.GetPaymentByOrderId;
using Domain.Enums;

namespace Application.Tests.Validation;

public class CommandAndQueryValidatorTests
{
    [Fact]
    public void ProcessPaymentValidator_ShouldFail_WhenRequiredFieldsMissing()
    {
        var validator = new ProcessPaymentCommandValidator();
        var command = new ProcessPaymentCommand("", "", 0, "US", PaymentMethod.Card, "", "bad-email");

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 5);
    }

    [Fact]
    public void RefundPaymentValidator_ShouldFail_WhenAmountInvalid()
    {
        var validator = new RefundPaymentCommandValidator();
        var command = new RefundPaymentCommand("", 0, "US", "", new string('a', 129));

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 4);
    }

    [Fact]
    public void ReconcilePendingPaymentsValidator_ShouldFail_WhenOutOfRange()
    {
        var validator = new ReconcilePendingPaymentsCommandValidator();
        var result = validator.Validate(new ReconcilePendingPaymentsCommand(0, 1001));

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void HandleStripeWebhookValidator_ShouldFail_WhenNoResolutionDataForKnownOutcome()
    {
        var validator = new HandleStripeWebhookCommandValidator();
        var command = new HandleStripeWebhookCommand(
            ProviderEventId: "evt-1",
            EventType: "payment_intent.succeeded",
            PayloadJson: "{}",
            Outcome: StripeWebhookOutcome.PaymentSucceeded,
            PaymentId: null,
            ProviderPaymentIntentId: null,
            ProviderRefundId: null,
            FailureCode: null,
            FailureMessage: null);

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("PaymentId, ProviderPaymentIntentId, or ProviderRefundId", StringComparison.Ordinal));
    }

    [Fact]
    public void EnqueueOrderCallbackValidator_ShouldFail_WhenRefundIdMissingForRefundCallbacks()
    {
        var validator = new EnqueueOrderCallbackCommandValidator();
        var result = validator.Validate(new EnqueueOrderCallbackCommand("pay-1", OrderCallbackType.RefundFailed, null, "ERR", "failed"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("RefundId is required", StringComparison.Ordinal));
    }

    [Fact]
    public void GetPaymentByIdValidator_ShouldFail_WhenPaymentIdMissing()
    {
        var validator = new GetPaymentByIdQueryValidator();
        var result = validator.Validate(new GetPaymentByIdQuery(""));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void GetPaymentByOrderIdValidator_ShouldFail_WhenIdempotencyKeyTooLong()
    {
        var validator = new GetPaymentByOrderIdQueryValidator();
        var result = validator.Validate(new GetPaymentByOrderIdQuery("order-1", new string('a', 129)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("must not exceed 128", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapturePaymentValidator_ShouldFail_WhenRequiredFieldsMissing()
    {
        var validator = new CapturePaymentCommandValidator();
        var command = new CapturePaymentCommand("", "", "", 0m, "US", "");

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 5);
    }

    [Fact]
    public void CapturePaymentValidator_ShouldFail_WhenAmountIsZero()
    {
        var validator = new CapturePaymentCommandValidator();
        var command = new CapturePaymentCommand("order-1", "customer-1", "pi_test_1", 0m, "USD", "idem-1");

        var result = validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("greater than zero", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapturePaymentValidator_ShouldPass_WhenAllFieldsValid()
    {
        var validator = new CapturePaymentCommandValidator();
        var command = new CapturePaymentCommand(
            OrderId: "order-1",
            CustomerId: "customer-1",
            ProviderPaymentIntentId: "pi_test_1",
            Amount: 99.99m,
            Currency: "USD",
            IdempotencyKey: "idem-1");

        var result = validator.Validate(command);

        Assert.True(result.IsValid);
    }
}
