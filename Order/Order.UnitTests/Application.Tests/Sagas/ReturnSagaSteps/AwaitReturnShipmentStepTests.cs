using Application.Sagas.Steps;
using Application.Common.Enums;
using Application.DTOs;
using Application.DTOs.ShipmentGateway;
using Application.Gateways;
using Application.Sagas.ReturnSaga;
using Application.Sagas.ReturnSaga.Steps;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.ReturnSagaSteps;

public class AwaitReturnShipmentStepTests
{
    private readonly IShippingGateway _shippingGateway = Substitute.For<IShippingGateway>();
    private readonly IShippingWebhookUrlProvider _shippingWebhookUrlProvider = Substitute.For<IShippingWebhookUrlProvider>();
    private readonly IIncidentReporter _incidentReporter = Substitute.For<IIncidentReporter>();
    private readonly ILogger<AwaitReturnShipmentStep> _logger = Substitute.For<ILogger<AwaitReturnShipmentStep>>();
    private readonly AwaitReturnShipmentStep _step;

    public AwaitReturnShipmentStepTests()
    {
        _shippingWebhookUrlProvider.GetReturnDeliveredCallbackUrl()
            .Returns("https://gateway.example.com/api/v1/webhooks/shipping/returns/delivered");

        _step = new AwaitReturnShipmentStep(_shippingGateway, _shippingWebhookUrlProvider, _incidentReporter, _logger);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenGatewaySucceeds()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext();
        var expectedShipmentId = "RETURN-SHIP-999";

        _shippingGateway.CreateReturnShipmentAsync(
            data.CorrelationId,
            data.CustomerId,
            data.ReturnedItems,
            data.ShippingCarrier,
            Arg.Any<CancellationToken>())
            .Returns(new ReturnShipmentResultDto(expectedShipmentId, "TRACK-1", DateTime.UtcNow.AddDays(2)));

        var result = await _step.ExecuteAsync(data, context, CancellationToken.None);
        
        Assert.IsType<WaitForEvent>(result);
        Assert.Equal(expectedShipmentId, context.ReturnShipmentId);

        await _shippingGateway.Received(1).CreateReturnShipmentAsync(
            data.CorrelationId,
            data.CustomerId,
            data.ReturnedItems,
            data.ShippingCarrier,
            Arg.Any<CancellationToken>());

        await _shippingGateway.Received().RegisterWebhookAsync(
            expectedShipmentId,
            Arg.Is<string>(url =>
                url.Contains("https://gateway.example.com/api/v1/webhooks/shipping/returns/delivered")
                && url.Contains($"orderId={data.CorrelationId}")),
            Arg.Is<string[]>(events => events.Contains("return.delivered")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipCreation_WhenShipmentIdAlreadyInContext_Idempotency()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext { ReturnShipmentId = "EXISTING-SHIP" };

        // gateway must still register webhook and register saga wait even if shipment existed
        var result = await _step.ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<WaitForEvent>(result);
        Assert.Equal("EXISTING-SHIP", context.ReturnShipmentId);

        await _shippingGateway.DidNotReceive().CreateReturnShipmentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<List<OrderItemDto>>(), Arg.Any<ShippingCarrier>(), Arg.Any<CancellationToken>());

        await _shippingGateway.Received(1).RegisterWebhookAsync(
            "EXISTING-SHIP",
            Arg.Is<string>(url =>
                url.Contains("https://gateway.example.com/api/v1/webhooks/shipping/returns/delivered")
                && url.Contains($"orderId={data.CorrelationId}")),
            Arg.Is<string[]>(events => events.Contains("return.delivered")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenShipmentCreationFails()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext();
        var errorMessage = "Shipping API Unavailable";

        _shippingGateway.CreateReturnShipmentAsync(
                Arg.Any<Guid>(), 
                Arg.Any<Guid>(), 
                Arg.Any<List<OrderItemDto>>(), 
                Arg.Any<ShippingCarrier>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception(errorMessage));

        var result = await _step.ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains(errorMessage, ((Fail)result).Reason);
        Assert.Null(context.ReturnShipmentId);

        await _shippingGateway.DidNotReceive().RegisterWebhookAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenWebhookRegistrationFails()
    {
        var data = CreateSampleData();

        _shippingGateway.CreateReturnShipmentAsync(
                Arg.Any<Guid>(), 
                Arg.Any<Guid>(), 
                Arg.Any<List<OrderItemDto>>(),
                Arg.Any<ShippingCarrier>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReturnShipmentResultDto("SHIP-OK", "TRACK-OK", DateTime.UtcNow.AddDays(1)));

        _shippingGateway.RegisterWebhookAsync(
                Arg.Any<string>(), 
                Arg.Any<string>(), 
                Arg.Any<string[]>(), 
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Webhook failed"));

        var result = await _step.ExecuteAsync(data, new ReturnSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("Webhook failed", ((Fail)result).Reason);
    }
    
    // compensation tests

    [Fact]
    public async Task CompensationAsync_ShouldCancelShipment_WhenShipmentIdExists()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext { ReturnShipmentId = "ReturnShipmentId" };

        await _step.CompensateAsync(data, context, CancellationToken.None);

        await _shippingGateway.Received(1).CancelReturnShipmentAsync(
            context.ReturnShipmentId,
            Arg.Is<string>(s => s.Contains("compensation")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldDoNothing_WhenShipmentIdIsEmpty()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext() { ReturnShipmentId = null };

        await _step.CompensateAsync(data, context, CancellationToken.None);
        await _shippingGateway.DidNotReceiveWithAnyArgs().CancelReturnShipmentAsync(default!, default!, default);
        
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("No return shipment to cancel")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CompensateAsync_ShouldCatchException_WhenGatewayFails()
    {
        var data = CreateSampleData();
        var context = new ReturnSagaContext { ReturnShipmentId = "ReturnShipmentId" };

        _shippingGateway.CancelReturnShipmentAsync(Arg.Any<string>(), Arg.Any<string>(), CancellationToken.None)
            .Throws(new Exception("Gateway Timeout"));
        
        var exception = await Record.ExceptionAsync(() =>
            _step.CompensateAsync(data, context, CancellationToken.None));

        Assert.Null(exception);
        
        _logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Failed to cancel")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
    
    private ReturnSagaData CreateSampleData()
    {
        return new ReturnSagaData
        {
            CorrelationId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            ReturnedItems = new List<OrderItemDto>
            {
                new OrderItemDto(Guid.NewGuid(), 1, 50, "USD")
            }
        };
    }
}