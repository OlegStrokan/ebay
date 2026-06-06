using Application.Gateways.Exceptions;
using Grpc.Core;
using Infrastructure.Gateways;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Protos.Payment;

namespace Infrastructure.Tests.Gateways;

public class PaymentGatewayTests
{
    private readonly PaymentService.PaymentServiceClient _client =
        Substitute.For<PaymentService.PaymentServiceClient>();

    private readonly ILogger<PaymentGateway> _logger =
        Substitute.For<ILogger<PaymentGateway>>();

    private PaymentGateway Build() => new(_client, _logger);

    // helpers
    
    private static AsyncUnaryCall<T> GrpcCall<T>(T response) =>
        new AsyncUnaryCall<T>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private static AsyncUnaryCall<T> GrpcFail<T>(StatusCode code, string detail) =>
        new AsyncUnaryCall<T>(
            Task.FromException<T>(new RpcException(new Status(code, detail))),
            Task.FromResult(new Metadata()),
            () => new Status(code, detail),
            () => new Metadata(),
            () => { });

    [Fact]
    public async Task ProcessPaymentAsync_ShouldReturnSucceededResult_WhenSucceeds()
    {
        var paymentId = "pay-123";
        ProcessPaymentRequest? capturedRequest = null;
        _client
            .ProcessPaymentAsync(Arg.Do<ProcessPaymentRequest>(x => capturedRequest = x),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse
            {
                Success = true,
                PaymentId = paymentId,
                Status = (ProcessPaymentStatus)1
            }));

        var result = await Build().ProcessPaymentAsync(
            Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None);

        Assert.Equal(paymentId, result.PaymentId);
        Assert.Equal(Application.Gateways.PaymentProcessingStatus.Succeeded, result.Status);
        Assert.Equal("USD", capturedRequest?.Currency);
        Assert.False(string.IsNullOrWhiteSpace(capturedRequest?.IdempotencyKey));
        Assert.Equal(string.Empty, capturedRequest?.CustomerEmail);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldReturnPendingResult_WhenProviderReturnsPending()
    {
        var paymentId = "pay-pending";
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse
            {
                Success = true,
                PaymentId = paymentId,
                Status = (ProcessPaymentStatus)2,
                ProviderPaymentIntentId = "pi_123"
            }));

        var result = await Build().ProcessPaymentAsync(
            Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None);

        Assert.Equal(paymentId, result.PaymentId);
        Assert.Equal(Application.Gateways.PaymentProcessingStatus.Pending, result.Status);
        Assert.Equal("pi_123", result.ProviderPaymentIntentId);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldReturnRequiresActionResult_WhenProviderRequiresAction()
    {
        var paymentId = "pay-3ds";
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse
            {
                Success = true,
                PaymentId = paymentId,
                Status = (ProcessPaymentStatus)4,
                ProviderPaymentIntentId = "pi_3ds",
                ClientSecret = "cs_3ds"
            }));

        var result = await Build().ProcessPaymentAsync(
            Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None);

        Assert.Equal(paymentId, result.PaymentId);
        Assert.Equal(Application.Gateways.PaymentProcessingStatus.RequiresAction, result.Status);
        Assert.Equal("pi_3ds", result.ProviderPaymentIntentId);
        Assert.Equal("cs_3ds", result.ClientSecret);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowInsufficientFunds_WhenErrorCodeIsInsufficientFunds()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse { Success = false, ErrorCode = "INSUFFICIENT_FUNDS" }));

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowPaymentDeclined_WhenErrorCodeIsPaymentDeclined()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse { Success = false, ErrorCode = "PAYMENT_DECLINED" }));

        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowInvalidOperation_WhenUnknownErrorCode()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new ProcessPaymentResponse { Success = false, ErrorCode = "UNKNOWN_ERROR" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowPaymentDeclined_WhenRpcInvalidArgument()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<ProcessPaymentResponse>(StatusCode.InvalidArgument, "bad payload"));

        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowInsufficientFunds_WhenRpcFailedPrecondition()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<ProcessPaymentResponse>(StatusCode.FailedPrecondition, "balance low"));

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowGatewayUnavailable_WithTimeoutReason_WhenRpcDeadlineExceeded()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<ProcessPaymentResponse>(StatusCode.DeadlineExceeded, "timeout"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ShouldThrowGatewayUnavailable_WithServiceUnavailableReason_WhenRpcUnavailable()
    {
        _client
            .ProcessPaymentAsync(Arg.Any<ProcessPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<ProcessPaymentResponse>(StatusCode.Unavailable, "service down"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().ProcessPaymentAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "CARD", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.ServiceUnavailable, ex.Reason);
    }

    [Fact]
    public async Task RefundAsync_ShouldReturnRefundId_WhenSucceeds()
    {
        var refundId = "ref-456";
        RefundPaymentRequest? capturedRequest = null;
        _client
            .RefundPaymentAsync(Arg.Do<RefundPaymentRequest>(x => capturedRequest = x),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new RefundPaymentResponse
            {
                Success = true,
                RefundId = refundId,
                Status = (RefundPaymentStatus)1,
            }));

        var result = await Build().RefundAsync("pay-123", 50m, "usd", "duplicate", CancellationToken.None);

        Assert.Equal(refundId, result);
        Assert.Equal("USD", capturedRequest?.Currency);
        Assert.False(string.IsNullOrWhiteSpace(capturedRequest?.IdempotencyKey));
    }

    [Fact]
    public async Task RefundWithStatusAsync_ShouldReturnPendingStatus_WhenProviderReturnsPending()
    {
        _client
            .RefundPaymentAsync(Arg.Any<RefundPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new RefundPaymentResponse
            {
                Success = true,
                RefundId = "ref-pending",
                Status = (RefundPaymentStatus)2,
                ProviderRefundId = "re_pending_123",
            }));

        var result = await Build().RefundWithStatusAsync(
            "pay-123",
            50m,
            "USD",
            "duplicate",
            CancellationToken.None);

        Assert.Equal("ref-pending", result.RefundId);
        Assert.Equal(Application.Gateways.RefundProcessingStatus.Pending, result.Status);
        Assert.Equal("re_pending_123", result.ProviderRefundId);
    }

    [Fact]
    public async Task RefundAsync_ShouldThrowInvalidOperation_WhenNotSuccess()
    {
        _client
            .RefundPaymentAsync(Arg.Any<RefundPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new RefundPaymentResponse { Success = false, ErrorMessage = "already refunded" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().RefundAsync("pay-123", 50m, "USD", "duplicate", CancellationToken.None));
    }

    [Fact]
    public async Task RefundAsync_ShouldThrowInvalidOperation_WhenRpcNotFound()
    {
        _client
            .RefundPaymentAsync(Arg.Any<RefundPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<RefundPaymentResponse>(StatusCode.NotFound, "payment not found"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().RefundAsync("pay-xyz", 50m, "USD", "test", CancellationToken.None));
    }

    [Fact]
    public async Task RefundWithStatusAsync_ShouldThrowGatewayUnavailable_WithTimeoutReason_WhenRpcDeadlineExceeded()
    {
        _client
            .RefundPaymentAsync(Arg.Any<RefundPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<RefundPaymentResponse>(StatusCode.DeadlineExceeded, "timeout"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().RefundWithStatusAsync("pay-xyz", 50m, "USD", "test", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task RefundWithStatusAsync_ShouldThrowGatewayUnavailable_WithServiceUnavailableReason_WhenRpcUnavailable()
    {
        _client
            .RefundPaymentAsync(Arg.Any<RefundPaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<RefundPaymentResponse>(StatusCode.Unavailable, "service down"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().RefundWithStatusAsync("pay-xyz", 50m, "USD", "test", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.ServiceUnavailable, ex.Reason);
    }

    // ---- CaptureAsync -----------------------------------------------------------

    [Fact]
    public async Task CaptureAsync_ShouldReturnSucceededResult_WhenGrpcSucceeds()
    {
        const string paymentId = "cap-pay-1";
        CapturePaymentRequest? capturedRequest = null;
        _client
            .CapturePaymentAsync(Arg.Do<CapturePaymentRequest>(x => capturedRequest = x),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new CapturePaymentResponse
            {
                Success = true,
                PaymentId = paymentId,
                Status = (ProcessPaymentStatus)1, // Succeeded
                ProviderPaymentIntentId = "pi_captured_1",
            }));

        var result = await Build().CaptureAsync(
            Guid.NewGuid(), Guid.NewGuid(), "pi_captured_1", 200m, "USD", CancellationToken.None);

        Assert.Equal(paymentId, result.PaymentId);
        Assert.Equal(Application.Gateways.PaymentProcessingStatus.Succeeded, result.Status);
        Assert.Equal("pi_captured_1", result.ProviderPaymentIntentId);
        Assert.Equal("USD", capturedRequest?.Currency);
        Assert.False(string.IsNullOrWhiteSpace(capturedRequest?.IdempotencyKey));
    }

    [Fact]
    public async Task CaptureAsync_ShouldThrowPaymentDeclinedException_WhenErrorCodeIsPaymentDeclined()
    {
        _client
            .CapturePaymentAsync(Arg.Any<CapturePaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new CapturePaymentResponse
            {
                Success = false,
                Status = (ProcessPaymentStatus)2,
                ErrorCode = "PAYMENT_DECLINED",
                ErrorMessage = "Card declined",
            }));

        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            Build().CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "pi_1", 100m, "USD", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureAsync_ShouldThrowInvalidOperation_WhenUnknownErrorCode()
    {
        _client
            .CapturePaymentAsync(Arg.Any<CapturePaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new CapturePaymentResponse
            {
                Success = false,
                Status = (ProcessPaymentStatus)2,
                ErrorCode = "UNKNOWN_CAPTURE_ERROR",
                ErrorMessage = "Something went wrong",
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "pi_1", 100m, "USD", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureAsync_ShouldThrowPaymentDeclined_WhenRpcInvalidArgument()
    {
        _client
            .CapturePaymentAsync(Arg.Any<CapturePaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<CapturePaymentResponse>(StatusCode.InvalidArgument, "invalid capture request"));

        await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            Build().CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "pi_1", 100m, "USD", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureAsync_ShouldThrowGatewayUnavailable_WithTimeoutReason_WhenRpcDeadlineExceeded()
    {
        _client
            .CapturePaymentAsync(Arg.Any<CapturePaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<CapturePaymentResponse>(StatusCode.DeadlineExceeded, "timeout"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "pi_1", 100m, "USD", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task CaptureAsync_ShouldThrowGatewayUnavailable_WithServiceUnavailableReason_WhenRpcUnavailable()
    {
        _client
            .CapturePaymentAsync(Arg.Any<CapturePaymentRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<CapturePaymentResponse>(StatusCode.Unavailable, "stripe down"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "pi_1", 100m, "USD", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.ServiceUnavailable, ex.Reason);
    }

    // ---- CancelAuthorizationAsync -----------------------------------------------

    [Fact]
    public async Task CancelAuthorizationAsync_ShouldCompleteSuccessfully_WhenGrpcSucceeds()
    {
        CancelAuthorizationRequest? capturedRequest = null;
        _client
            .CancelAuthorizationAsync(Arg.Do<CancelAuthorizationRequest>(x => capturedRequest = x),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new CancelAuthorizationResponse { Success = true }));

        var exception = await Record.ExceptionAsync(() =>
            Build().CancelAuthorizationAsync("pi_auth_1", CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal("pi_auth_1", capturedRequest?.ProviderPaymentIntentId);
    }

    [Fact]
    public async Task CancelAuthorizationAsync_ShouldThrowInvalidOperation_WhenServiceReturnsNotSuccess()
    {
        _client
            .CancelAuthorizationAsync(Arg.Any<CancelAuthorizationRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcCall(new CancelAuthorizationResponse
            {
                Success = false,
                ErrorMessage = "authorization already captured",
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build().CancelAuthorizationAsync("pi_auth_2", CancellationToken.None));
    }

    [Fact]
    public async Task CancelAuthorizationAsync_ShouldThrowGatewayUnavailable_WithTimeoutReason_WhenRpcDeadlineExceeded()
    {
        _client
            .CancelAuthorizationAsync(Arg.Any<CancelAuthorizationRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<CancelAuthorizationResponse>(StatusCode.DeadlineExceeded, "timeout"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().CancelAuthorizationAsync("pi_auth_3", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task CancelAuthorizationAsync_ShouldThrowGatewayUnavailable_WithServiceUnavailableReason_WhenRpcUnavailable()
    {
        _client
            .CancelAuthorizationAsync(Arg.Any<CancelAuthorizationRequest>(),
                Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(GrpcFail<CancelAuthorizationResponse>(StatusCode.Unavailable, "service down"));

        var ex = await Assert.ThrowsAsync<GatewayUnavailableException>(() =>
            Build().CancelAuthorizationAsync("pi_auth_4", CancellationToken.None));

        Assert.Equal(GatewayUnavailableReason.ServiceUnavailable, ex.Reason);
    }
}
