using Application.Sagas.Steps;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.OrderSagaSteps;

public class AuthorizePaymentStepTests
{
    private readonly IPaymentGateway _paymentGateway = Substitute.For<IPaymentGateway>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<AuthorizePaymentStep> _logger = Substitute.For<ILogger<AuthorizePaymentStep>>();

    private AuthorizePaymentStep BuildStep() => new(_paymentGateway, _incidentReporter, _logger);

    // ---- frontend pre-auth path -------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldRecordHold_WhenFrontendPaymentIntentIsPresent()
    {
        var data = CreateSampleDataWithPaymentIntent("pi_fe");
        var context = new OrderSagaContext();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(OrderSagaPaymentStatus.Authorized, context.PaymentStatus);
        Assert.Equal("pi_fe", context.ProviderPaymentIntentId);

        // The hold already exists (created by the browser) — no backend call.
        await _paymentGateway.DidNotReceive().AuthorizeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- backend-initiated authorize -------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldAuthorize_WhenBackendInitiated()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                data.CorrelationId, data.CustomerId, data.TotalAmount,
                data.Currency, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult("PAY-1", PaymentProcessingStatus.Authorized, "pi_auth"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(OrderSagaPaymentStatus.Authorized, context.PaymentStatus);
        Assert.Equal("PAY-1", context.PaymentId);
        Assert.Equal("pi_auth", context.ProviderPaymentIntentId);

        await _paymentGateway.Received(1).AuthorizeAsync(
            data.CorrelationId, data.CustomerId, data.TotalAmount,
            data.Currency, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTreatImmediateSettleAsAuthorized_ForInvoiceLikeMethods()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult("PAY-INV", PaymentProcessingStatus.Succeeded, "pi_inv"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(OrderSagaPaymentStatus.Authorized, context.PaymentStatus);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldWait_WhenAuthorizationIsPending()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult("PAY-P", PaymentProcessingStatus.Pending, "pi_p"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<WaitForEvent>(result);
        Assert.Equal(OrderSagaPaymentStatus.Pending, context.PaymentStatus);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldWait_WhenAuthorizationRequiresAction()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentProcessingResult("PAY-3DS", PaymentProcessingStatus.RequiresAction, "pi_3ds", "cs_3ds"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<WaitForEvent>(result);
        Assert.Equal(OrderSagaPaymentStatus.Pending, context.PaymentStatus);
        Assert.Equal("cs_3ds", context.PaymentClientSecret);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFail_WhenAuthorizationDeclined()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new PaymentDeclinedException("Card declined"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        var fail = Assert.IsType<Fail>(result);
        Assert.Contains("Card declined", fail.Reason);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
        Assert.Equal("PAYMENT_DECLINED", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFail_WhenInsufficientFunds()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InsufficientFundsException("Not enough balance"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        var fail = Assert.IsType<Fail>(result);
        Assert.Contains("Not enough balance", fail.Reason);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
        Assert.Equal("INSUFFICIENT_FUNDS", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkUncertainAndWait_WhenGatewayTimeoutOccurs()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.Timeout, "deadline exceeded"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<WaitForEvent>(result);
        Assert.Equal(OrderSagaPaymentStatus.Uncertain, context.PaymentStatus);
        Assert.Equal("AUTH_RESULT_UNCERTAIN", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFail_WhenGatewayServiceIsUnavailable()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new GatewayUnavailableException(GatewayUnavailableReason.ServiceUnavailable, "service unavailable"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
        Assert.Equal("PAYMENT_GATEWAY_UNAVAILABLE", context.PaymentFailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFail_WhenUnexpectedExceptionOccurs()
    {
        var context = new OrderSagaContext();

        _paymentGateway.AuthorizeAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Payment provider unreachable"));

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        var fail = Assert.IsType<Fail>(result);
        Assert.Contains("Payment provider unreachable", fail.Reason);
        Assert.Equal(OrderSagaPaymentStatus.Failed, context.PaymentStatus);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkip_WhenAlreadyAuthorized_Idempotency()
    {
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
            ProviderPaymentIntentId = "pi_existing",
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        await _paymentGateway.DidNotReceive().AuthorizeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- compensation: void the hold -------------------------------------------

    [Fact]
    public async Task CompensateAsync_ShouldVoidHold_WhenAuthorized()
    {
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
            ProviderPaymentIntentId = "pi_void",
        };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _paymentGateway.Received(1).CancelAuthorizationAsync("pi_void", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldVoidFrontendHold_UsingDataIntentId()
    {
        var data = CreateSampleDataWithPaymentIntent("pi_fe_void");
        var context = new OrderSagaContext { PaymentStatus = OrderSagaPaymentStatus.Authorized };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _paymentGateway.Received(1).CancelAuthorizationAsync("pi_fe_void", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotVoid_WhenAlreadyCaptured()
    {
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
            ProviderPaymentIntentId = "pi_captured",
        };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _paymentGateway.DidNotReceive().CancelAuthorizationAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldDoNothing_WhenNothingWasHeld()
    {
        var context = new OrderSagaContext { PaymentStatus = OrderSagaPaymentStatus.Failed };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _paymentGateway.DidNotReceive().CancelAuthorizationAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldRaiseIncident_WhenVoidFails()
    {
        var context = new OrderSagaContext
        {
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
            ProviderPaymentIntentId = "pi_fail",
        };

        _paymentGateway.CancelAuthorizationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("void failed"));

        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None));

        Assert.Null(exception);
        await _incidentReporter.Received(1).SendAlertAsync(
            Arg.Is<IncidentAlert>(a => a.AlertType == "AuthorizationVoidFailed"),
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
}
