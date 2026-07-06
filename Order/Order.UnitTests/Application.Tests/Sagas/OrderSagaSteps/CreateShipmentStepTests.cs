using Application.Sagas.Steps;
using Application.Common.Enums;
using Application.DTOs;
using Application.DTOs.ShipmentGateway;
using Application.Gateways;
using Application.Gateways.Exceptions;
using Application.Interfaces;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Domain.Entities.Order;
using Domain.Exceptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.OrderSagaSteps;

public class CreateShipmentStepTests
{
    private readonly IShippingGateway _shippingGateway = Substitute.For<IShippingGateway>();
    private readonly IOrderPersistenceService _orderPersistenceService = Substitute.For<IOrderPersistenceService>();
    private readonly IPplPendingBookingRepository _pplPendingBookingRepository = Substitute.For<IPplPendingBookingRepository>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<CreateShipmentStep> _logger = Substitute.For<ILogger<CreateShipmentStep>>();

    private CreateShipmentStep BuildStep() =>
        new(_shippingGateway, _orderPersistenceService, _pplPendingBookingRepository, _incidentReporter, _logger);

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_AndUpdateOrder_WhenGatewaySucceeds()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();
        var expectedShipmentId = "SHIP-123";
        var expectedTracking = "TRACK-ABC";

        _shippingGateway
            .CreateShipmentAsync(data.CorrelationId, data.DeliveryAddress, data.Items, data.ShippingCarrier, Arg.Any<CancellationToken>())
            .Returns(new ShipmentResultDto(expectedShipmentId, expectedTracking));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(expectedShipmentId, context.ShipmentId);
        Assert.Equal(expectedTracking, context.TrackingNumber);
        Assert.Equal(expectedShipmentId, ((Completed)result).Data?["ShipmentId"]);

        await _shippingGateway.Received(1).CreateShipmentAsync(
            data.CorrelationId, data.DeliveryAddress, data.Items, data.ShippingCarrier, Arg.Any<CancellationToken>());

        // order must be updated with tracking via persistence service
        await _orderPersistenceService.Received(1).UpdateOrderAsync(
            data.CorrelationId, Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipGateway_WhenShipmentIdAlreadyInContext_Idempotency()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext { ShipmentId = "EXISTING-SHIP", TrackingNumber = "EXISTING-TRACK" };

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);

        // gateway must NOT be called again
        await _shippingGateway.DidNotReceive().CreateShipmentAsync(
            Arg.Any<Guid>(), Arg.Any<AddressDto>(), Arg.Any<IReadOnlyCollection<OrderItemDto>>(), Arg.Any<ShippingCarrier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenInvalidAddressExceptionThrown()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _shippingGateway
            .CreateShipmentAsync(Arg.Any<Guid>(), Arg.Any<AddressDto>(), Arg.Any<IReadOnlyCollection<OrderItemDto>>(), Arg.Any<ShippingCarrier>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidAddressException("Bad address"));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Bad address", ((Fail)result).Reason);
        Assert.Null(context.ShipmentId);

        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenOrderNotFound()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext();

        _shippingGateway
            .CreateShipmentAsync(Arg.Any<Guid>(), Arg.Any<AddressDto>(), Arg.Any<IReadOnlyCollection<OrderItemDto>>(), Arg.Any<ShippingCarrier>(), Arg.Any<CancellationToken>())
            .Returns(new ShipmentResultDto("SHIP-1", "TRACK-1"));

        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new OrderNotFoundException(data.CorrelationId));

        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("not found", ((Fail)result).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenUnexpectedExceptionThrown()
    {
        var data = CreateSampleData();

        _shippingGateway
            .CreateShipmentAsync(Arg.Any<Guid>(), Arg.Any<AddressDto>(), Arg.Any<IReadOnlyCollection<OrderItemDto>>(), Arg.Any<ShippingCarrier>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Shipping API is on fire"));

        var result = await BuildStep().ExecuteAsync(data, new OrderSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Shipping API is on fire", ((Fail)result).Reason);
    }

    [Fact]
    public async Task CompensateAsync_ShouldDoNothing_WhenShipmentIdIsEmpty()
    {
        var context = new OrderSagaContext { ShipmentId = null };

        await BuildStep().CompensateAsync(CreateSampleData(), context, CancellationToken.None);

        await _shippingGateway.DidNotReceiveWithAnyArgs().CancelShipmentAsync(default!, default);
        await _orderPersistenceService.DidNotReceive().UpdateOrderAsync(
            Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldCancelShipmentAndRevertTracking_WhenShipmentExists()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext { ShipmentId = "SHIP-TO-CANCEL" };

        await BuildStep().CompensateAsync(data, context, CancellationToken.None);

        await _shippingGateway.Received(1).CancelShipmentAsync(
            "SHIP-TO-CANCEL", Arg.Any<CancellationToken>());

        await _orderPersistenceService.Received(1).UpdateOrderAsync(
            data.CorrelationId, Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldStillRevertTracking_WhenGatewayCancellationFails()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext { ShipmentId = "SHIP-FAIL" };

        _shippingGateway
            .CancelShipmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("Shipping API down"));

        // must NOT throw
        var exception = await Record.ExceptionAsync(() => BuildStep().CompensateAsync(data, context, CancellationToken.None));

        Assert.Null(exception);

        // order tracking still reverted even though gateway failed
        await _orderPersistenceService.Received(1).UpdateOrderAsync(
            data.CorrelationId, Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldNotThrow_WhenOrderNotFoundDuringCompensation()
    {
        var data = CreateSampleData();
        var context = new OrderSagaContext { ShipmentId = "SHIP-123" };

        _orderPersistenceService
            .UpdateOrderAsync(Arg.Any<Guid>(), Arg.Any<Func<Order, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new OrderNotFoundException(data.CorrelationId));

        var exception = await Record.ExceptionAsync(() => BuildStep().CompensateAsync(data, context, CancellationToken.None));

        Assert.Null(exception);
    }
    
    private static OrderSagaData CreateSampleData() => new()
    {
        CorrelationId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        DeliveryAddress = new AddressDto("Baker St", "London", "UK", "NW1"),
        Items = new List<OrderItemDto> { new(Guid.NewGuid(), 1, 100, "USD") }
    };
}