using Application.Sagas.Steps;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Models;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.OrderSagaSteps;

public class CapturePaymentStepTests
{
    private readonly IPaymentGateway _paymentGateway = Substitute.For<IPaymentGateway>();
    private readonly ICompensationRefundRetryRepository _compensationRefundRetryRepository = Substitute.For<ICompensationRefundRetryRepository>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<CapturePaymentStep> _logger = Substitute.For<ILogger<CapturePaymentStep>>();

    public CapturePaymentStepTests()
    {
        _paymentGateway
            .CancelAuthorizationAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _paymentGateway
            .RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new RefundProcessingResult("REF-DEFAULT", RefundProcessingStatus.Succeeded));

        _compensationRefundRetryRepository
            .EnqueueIfNotExistsAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => CompensationRefundRetry.Create(
                callInfo.ArgAt<Guid>(0),
                callInfo.ArgAt<string>(1),
                callInfo.ArgAt<decimal>(2),
                callInfo.ArgAt<string>(3),
                callInfo.ArgAt<string>(4)));
    }

    private CapturePaymentStep BuildStep() => new(
        _paymentGateway,
        _compensationRefundRetryRepository,
        _incidentReporter,
        _logger);

    [Fact]
    public async Task ExecuteAsync_ShouldSkipGateway_WhenPaymentAlreadySucceeded_Idempotency()
    {
        var context = new OrderSagaContext
        {
            PaymentId = "EXISTING_PAY",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Completed>(result);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTreatLegacyPaymentIdAsSucceeded_WhenStatusNotStarted()
    {
        var context = new OrderSagaContext
        {
            PaymentId = "LEGACY_PAY",
            PaymentStatus = OrderSagaPaymentStatus.NotStarted,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(OrderSagaPaymentStatus.Succeeded, context.PaymentStatus);

        await _paymentGateway.DidNotReceive().ProcessPaymentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldCallRefund_WhenPaymentSucceeded()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _paymentGateway.Received(1).RefundWithStatusAsync(
            "PAY-123",
            data.TotalAmount,
            data.Currency,
            Arg.Is<string>(s => s.Contains("saga compensation")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotCallRefund_WhenPaymentIdIsEmpty()
    {
        var context = new OrderSagaContext { PaymentId = null };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _paymentGateway.DidNotReceive().RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotCallRefund_WhenPaymentWasNotSucceeded()
    {
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-PENDING",
            PaymentStatus = OrderSagaPaymentStatus.Pending,
        };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _paymentGateway.DidNotReceive().RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldEnqueueDeferredVerification_WhenPaymentStatusIsUncertain_AndPaymentIdExists()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-UNCERTAIN",
            PaymentStatus = OrderSagaPaymentStatus.Uncertain,
        };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _compensationRefundRetryRepository.Received(1).EnqueueIfNotExistsAsync(
            data.CorrelationId,
            "PAY-UNCERTAIN",
            data.TotalAmount,
            data.Currency,
            Arg.Is<string>(s => s.Contains("Uncertain payment verification")),
            Arg.Any<CancellationToken>());

        await _paymentGateway.DidNotReceive().RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldCancelAuthorization_WhenPaymentStatusIsUncertain_AndOnlyIntentIdExists()
    {
        var data = CreateSampleDataWithPaymentIntent("pi_uncertain_123");
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Uncertain,
            PaymentId = null,
            ProviderPaymentIntentId = "pi_uncertain_123",
        };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _paymentGateway.Received(1).CancelAuthorizationAsync(
            "pi_uncertain_123",
            Arg.Any<CancellationToken>());

        await _paymentGateway.DidNotReceive().RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _compensationRefundRetryRepository.DidNotReceive().EnqueueIfNotExistsAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldRaiseIncident_WhenPaymentStatusIsUncertain_AndNoIdentifiersExist()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Uncertain,
            PaymentId = null,
            ProviderPaymentIntentId = null,
        };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _incidentReporter.Received(1).SendAlertAsync(
            Arg.Is<IncidentAlert>(a => a.AlertType == "PaymentCompensationUncertain"),
            Arg.Any<CancellationToken>());

        await _paymentGateway.DidNotReceive().RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldRefundLegacyPayment_WhenStatusNotStartedButPaymentExists()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentId = "LEGACY-PAY",
            PaymentStatus = OrderSagaPaymentStatus.NotStarted,
        };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        Assert.Equal(OrderSagaPaymentStatus.Succeeded, context.PaymentStatus);
        await _paymentGateway.Received(1).RefundWithStatusAsync(
            "LEGACY-PAY",
            data.TotalAmount,
            data.Currency,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotThrow_WhenGatewayRefundFails()
    {
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        _paymentGateway.RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Refund service down"));

        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None));

        Assert.Null(exception);

        await _paymentGateway.Received(1).RefundWithStatusAsync(
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _incidentReporter.Received(1).SendAlertAsync(
            Arg.Is<IncidentAlert>(x => x.AlertType == "PaymentRefundCompensationFailed"),
            Arg.Any<CancellationToken>());

        await _compensationRefundRetryRepository.DidNotReceive().EnqueueIfNotExistsAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldEnqueueVerification_WhenRefundIsPending()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new RefundProcessingResult("REF-PENDING", RefundProcessingStatus.Pending));

        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(data, context, CancellationToken.None));

        Assert.Null(exception);

        await _compensationRefundRetryRepository.Received(1).EnqueueIfNotExistsAsync(
            data.CorrelationId,
            "PAY-123",
            data.TotalAmount,
            data.Currency,
            Arg.Is<string>(s => s.Contains("Pending refund verification")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldEnqueueRetry_WhenRefundFailsWithTransientGatewayError()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext
        {
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        _paymentGateway.RefundWithStatusAsync(
                Arg.Any<string>(),
                Arg.Any<decimal>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.Timeout, "deadline exceeded"));

        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(data, context, CancellationToken.None));

        Assert.Null(exception);

        await _compensationRefundRetryRepository.Received(1).EnqueueIfNotExistsAsync(
            data.CorrelationId,
            "PAY-123",
            data.TotalAmount,
            data.Currency,
            Arg.Is<string>(x => x.Contains("saga compensation")),
            Arg.Any<CancellationToken>());

        await _incidentReporter.DidNotReceive().SendAlertAsync(
            Arg.Any<IncidentAlert>(),
            Arg.Any<CancellationToken>());
    }


    private static OrderSagaData CreateSampleData() => new()
    {
        CorrelationId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        TotalAmount = 150m,
        Currency = "USD",
        PaymentMethod = Application.Common.Enums.PaymentMethod.CreditCard,
        DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
        Items = new List<OrderItemDto> { new(Guid.NewGuid(), 1, 150m, "USD") }
    };

    private static OrderSagaData CreateSampleDataWithPaymentIntent(string paymentIntentId = "pi_test_123") => new()
    {
        CorrelationId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        TotalAmount = 150m,
        Currency = "USD",
        PaymentMethod = Application.Common.Enums.PaymentMethod.CreditCard,
        PaymentIntentId = paymentIntentId,
        DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
        Items = new List<OrderItemDto> { new(Guid.NewGuid(), 1, 150m, "USD") }
    };

    // ---- B2C capture path -------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldCallCaptureAsync_WhenPaymentIntentIdIsPresent()
    {
        var data = CreateSampleDataWithPaymentIntent("pi_abc");
        var context = new OrderSagaContext();

        _paymentGateway.CaptureAsync(
                data.CorrelationId, data.CustomerId, "pi_abc",
                data.TotalAmount, data.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult(
                PaymentId: "PAY-CAPTURE-1",
                Status: PaymentProcessingStatus.Succeeded,
                ProviderPaymentIntentId: "pi_abc"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(OrderSagaPaymentStatus.Succeeded, context.PaymentStatus);
        Assert.Equal("PAY-CAPTURE-1", context.PaymentId);
        Assert.Equal("pi_abc", context.ProviderPaymentIntentId);

        await _paymentGateway.Received(1).CaptureAsync(
            data.CorrelationId, data.CustomerId, "pi_abc",
            data.TotalAmount, data.Currency, Arg.Any<CancellationToken>());

        await _paymentGateway.DidNotReceive().ProcessPaymentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompleted_WithPaymentIdInData_WhenCaptureSucceeds()
    {
        var data = CreateSampleDataWithPaymentIntent();
        var context = new OrderSagaContext();

        _paymentGateway.CaptureAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult("PAY-99", PaymentProcessingStatus.Succeeded, "pi_test_123"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        var completed = Assert.IsType<Completed>(result);
        Assert.Equal("PAY-99", completed.Data?["PaymentId"]);
        Assert.Equal(data.TotalAmount, completed.Data?["Amount"]);
        Assert.Equal(data.Currency, completed.Data?["Currency"]);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFail_AndMarkFailed_WhenCaptureReturnsFailed()
    {
        var data = CreateSampleDataWithPaymentIntent();
        var context = new OrderSagaContext();

        _paymentGateway.CaptureAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult(
                PaymentId: null,
                Status: PaymentProcessingStatus.Failed,
                ErrorCode: "capture_failed",
                ErrorMessage: "Card capture declined"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        var fail = Assert.IsType<Fail>(result);
        Assert.Contains("Card capture declined", fail.Reason);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
        Assert.Equal("capture_failed", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipCaptureAndReturnCompleted_WhenPaymentAlreadySucceeded_WithPaymentIntentId()
    {
        var data = CreateSampleDataWithPaymentIntent();
        var context = new OrderSagaContext
        {
            PaymentId = "ALREADY_CAPTURED",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        await _paymentGateway.DidNotReceive().CaptureAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateGatewayExceptions_FromCapturePath()
    {
        var data = CreateSampleDataWithPaymentIntent();
        var context = new OrderSagaContext();

        _paymentGateway.CaptureAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new PaymentDeclinedException("Capture declined by issuer"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        var fail = Assert.IsType<Fail>(result);
        Assert.Contains("Capture declined by issuer", fail.Reason);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
        Assert.Equal("PAYMENT_DECLINED", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkUncertainAndWait_WhenCaptureTimesOut()
    {
        var data = CreateSampleDataWithPaymentIntent();
        var context = new OrderSagaContext();

        _paymentGateway.CaptureAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.Timeout, "capture timeout"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<WaitForEvent>(result);
        Assert.Equal(OrderSagaPaymentStatus.Uncertain, context.PaymentStatus);
        Assert.Equal("PAYMENT_RESULT_UNCERTAIN", context.PaymentFailureCode);
    }
}