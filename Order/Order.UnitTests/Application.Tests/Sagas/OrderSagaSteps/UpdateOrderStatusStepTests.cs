using Application.Sagas.Steps;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Domain.Entities;
using Domain.Entities.Order;
using Domain.Exceptions;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.OrderSagaSteps;

public class UpdateOrderStatusStepTests
{
    private readonly IInventoryGateway _inventoryGateway =
        Substitute.For<IInventoryGateway>();

    private readonly ILogger<UpdateOrderStatusStep> _logger =
        Substitute.For<ILogger<UpdateOrderStatusStep>>();

    private UpdateOrderStatusStep BuildStep() =>
        new(_inventoryGateway, _logger);

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenPaymentAuthorizedAndInventoryConfirmed()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(true, ((Completed)result).Data?["InventoryConfirmed"]);
        Assert.True(context.OrderStatusUpdated);

        await _inventoryGateway.Received(1).ConfirmReservationAsync(
            "RES-123",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenPaymentAlreadySucceeded()
    {
        // B2B / recurring path: capture happened synchronously, status is Succeeded at step 4.
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.True(context.OrderStatusUpdated);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkip_WhenOrderStatusAlreadyUpdated_Idempotency()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
            OrderStatusUpdated = true,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Completed>(result);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenPaymentNotYetAuthorized()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Pending,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Payment must be authorized", ((Fail)result).Reason);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenReservationIdMissingInContext()
    {
        var context = new OrderSagaContext
        {
            ReservationId = null,
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
        };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Inventory reservation ID not found", ((Fail)result).Reason);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenUnexpectedExceptionOccurs()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
        };

        _inventoryGateway
            .ConfirmReservationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Database timeout"));

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Database timeout", ((Fail)result).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenInventoryConfirmationFails()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
        };

        _inventoryGateway
            .ConfirmReservationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("expired"));

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("expired", ((Fail)result).Reason);
        Assert.False(context.ReservationConfirmed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipConfirmation_WhenReservationAlreadyConfirmed()
    {
        // Simulates a saga resume where confirm succeeded but the process crashed
        // before OrderStatusUpdated was persisted.
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentStatus = OrderSagaPaymentStatus.Authorized,
            ReservationConfirmed = true,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.True(context.OrderStatusUpdated);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldBeNoOp_BecauseCancelOrderOnFailureStepHandlesCancellation()
    {
        await BuildStep().CompensateAsync(CreateSampleData(), new OrderSagaContext(), CancellationToken.None);

        // UpdateOrderStatusStep.CompensateAsync is intentionally a no-op:
        // order cancellation is centralised in CancelOrderOnFailureStep (Order: 0),
        // which runs for every compensation regardless of which step failed.
        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotThrow()
    {
        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(CreateSampleData(), new OrderSagaContext(), CancellationToken.None));

        Assert.Null(exception);
    }

    private static OrderSagaData CreateSampleData() => new()
    {
        CorrelationId = Guid.NewGuid(),
        DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
        Items = new List<OrderItemDto>()
    };
}
