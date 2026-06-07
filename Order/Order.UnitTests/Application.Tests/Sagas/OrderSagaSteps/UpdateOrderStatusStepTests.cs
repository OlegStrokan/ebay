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

    private readonly IOrderPersistenceService _orderPersistenceService =
        Substitute.For<IOrderPersistenceService>();

    private readonly ILogger<UpdateOrderStatusStep> _logger =
        Substitute.For<ILogger<UpdateOrderStatusStep>>();

    private UpdateOrderStatusStep BuildStep() =>
        new(_inventoryGateway, _orderPersistenceService, _logger);
    
    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenOrderIsUpdated()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal("Paid", ((Completed)result).Data?["Status"]);
        Assert.True(context.OrderStatusUpdated);

        await _inventoryGateway.Received(1).ConfirmReservationAsync(
            "RES-123",
            Arg.Any<CancellationToken>());

        await _orderPersistenceService.Received(1).UpdateOrderAsync(
            data.CorrelationId,
            Arg.Any<Func<Order, Task>>(),
            Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            _orderPersistenceService.UpdateOrderAsync(
                data.CorrelationId,
                Arg.Any<Func<Order, Task>>(),
                Arg.Any<CancellationToken>());

            _inventoryGateway.ConfirmReservationAsync(
                "RES-123",
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkip_WhenOrderStatusAlreadyUpdated_Idempotency()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
            OrderStatusUpdated = true,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenPaymentNotSucceeded()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Pending,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Payment is not confirmed as succeeded", ((Fail)result).Reason);

        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenPaymentIdMissingInContext()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = null,
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Payment ID not found", ((Fail)result).Reason);

        // persistence must not be touched when context is incomplete
        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenReservationIdMissingInContext()
    {
        var context = new OrderSagaContext
        {
            ReservationId = null,
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };
        var data = CreateSampleData();

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Inventory reservation ID not found", ((Fail)result).Reason);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenOrderNotFound()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };
        var data = CreateSampleData();

        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new OrderNotFoundException(data.CorrelationId));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Critical Error", ((Fail)result).Reason);

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
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };
        var data = CreateSampleData();

        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Database timeout"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Database timeout", ((Fail)result).Reason);

        await _inventoryGateway.DidNotReceive().ConfirmReservationAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenInventoryConfirmationFails()
    {
        var context = new OrderSagaContext
        {
            ReservationId = "RES-123",
            PaymentId = "PAY-123",
            PaymentStatus = OrderSagaPaymentStatus.Succeeded,
        };

        _inventoryGateway
            .ConfirmReservationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("expired"));

        var result = await BuildStep().ExecuteAsync(CreateSampleData(), context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("expired", ((Fail)result).Reason);

        await _orderPersistenceService.Received(1).UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }
    
    [Fact]
    public async Task CompensateAsync_ShouldBeNoOp_BecauseCancelOrderOnFailureStepHandlesCancellation()
    {
        var data = CreateSampleData();

        await BuildStep().CompensateAsync(data, new OrderSagaContext(), CancellationToken.None);

        // UpdateOrderStatusStep.CompensateAsync is intentionally a no-op:
        // order cancellation is centralised in CancelOrderOnFailureStep (Order: 0),
        // which runs for every compensation regardless of which step failed.
        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(),
            Arg.Any<Func<Order, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotThrow_WhenOrderNotFound()
    {
        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new OrderNotFoundException(Guid.NewGuid()));

        var exception = await Record.ExceptionAsync(() =>
            BuildStep().CompensateAsync(CreateSampleData(), new OrderSagaContext(), CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotThrow_WhenUnexpectedExceptionOccurs()
    {
        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DB crashed"));

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