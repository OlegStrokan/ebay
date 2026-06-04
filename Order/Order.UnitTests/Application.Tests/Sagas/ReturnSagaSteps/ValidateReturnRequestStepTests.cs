using Application.Sagas.Steps;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Application.Sagas.ReturnSaga;
using Application.Sagas.ReturnSaga.Steps;
using Domain.Entities;
using Domain.Entities.Order;
using Domain.Entities.RequestReturn;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Sagas.ReturnSagaSteps;

public class ValidateReturnRequestStepTests
{
    private readonly IOrderPersistenceService _orderPersistenceService =
        Substitute.For<IOrderPersistenceService>();

    private readonly IReturnRequestPersistenceService _returnRequestPersistenceService =
        Substitute.For<IReturnRequestPersistenceService>();

    private readonly IUserGateway _userGateway =
        Substitute.For<IUserGateway>();

    private readonly ReturnPolicyService _returnPolicyService = new();
    private readonly ILogger<ValidateReturnRequestStep> _logger =
        Substitute.For<ILogger<ValidateReturnRequestStep>>();

    private ValidateReturnRequestStep BuildStep() => new(
        _orderPersistenceService,
        _returnRequestPersistenceService,
        _userGateway,
        _returnPolicyService,
        _logger);
        
    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenOrderIsCompletedAndEligible()
    {
        var order = CreateCompleteOrder();
        var productId = order.Items.First().ProductId.Value;
        var data = CreateSampleData(order.Id.Value, productId);

        _orderPersistenceService
            .LoadOrderAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns(order);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        SetupUserGateway(data.CustomerId);

        var context = new ReturnSagaContext();
        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal(data.CorrelationId, ((Completed)result).Data?["OrderId"]);
        Assert.True(context.ReturnRequestValidated);

        await _returnRequestPersistenceService.Received(1).CreateReturnRequestAsync(
            Arg.Any<RequestReturn>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkip_WhenAlreadyValidated_Idempotency()
    {
        var context = new ReturnSagaContext { ReturnRequestValidated = true };

        var result = await BuildStep().ExecuteAsync(CreateSampleData(Guid.NewGuid()), context, CancellationToken.None);

        Assert.IsType<Completed>(result);

        await _orderPersistenceService.DidNotReceive()
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenReturnRequestAlreadyExists()
    {
        var order = CreateCompleteOrder();
        
        // idempotency test
        var data = CreateSampleData(order.Id.Value);
        var existingRequest = CreateReturnRequest(order);

        _orderPersistenceService
            .LoadOrderAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns(order);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(order.Id.Value, Arg.Any<CancellationToken>())
            .Returns(existingRequest);

        var context = new ReturnSagaContext();
        var result = await BuildStep().ExecuteAsync(data, context, CancellationToken.None);

        Assert.IsType<Completed>(result);
        Assert.Equal("ExistingRecord", ((Completed)result).Data?["Source"]);
        Assert.True(context.ReturnRequestValidated);

        await _returnRequestPersistenceService.DidNotReceive().CreateReturnRequestAsync(
            Arg.Any<RequestReturn>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenOrderNotFound()
    {
        var data = CreateSampleData(Guid.NewGuid());

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await BuildStep().ExecuteAsync(data, new ReturnSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("not found", ((Fail)result).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenOrderStatusIsNotCompleted()
    {
        var order = CreatePendingOrder();
        var data = CreateSampleData(order.Id.Value);

        _orderPersistenceService
            .LoadOrderAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns(order);

        var result = await BuildStep().ExecuteAsync(data, new ReturnSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("must be completed", ((Fail)result).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenOrderNotEligibleForReturn()
    {
        // order completed, but return window has passed (simulate by creating order with old timestamp)
        var order = CreateCompleteOrder(DateTime.UtcNow.AddDays(-20));
        var data = CreateSampleData(order.Id.Value);

        _orderPersistenceService
            .LoadOrderAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns(order);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(data.CorrelationId, Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        var result = await BuildStep().ExecuteAsync(data, new ReturnSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("not eligible", ((Fail)result).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenUnexpectedExceptionOccurs()
    {
        var data = CreateSampleData(Guid.NewGuid());

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("DB connection lost"));

        var result = await BuildStep().ExecuteAsync(data, new ReturnSagaContext(), CancellationToken.None);

        Assert.IsType<Fail>(result);
        Assert.Contains("DB connection lost", ((Fail)result).Reason);
    }

    [Fact]
    public async Task CompensateAsync_ShouldDoNothing_ValidateIsNotReversible()
    {
        var data = CreateSampleData(Guid.NewGuid());

        await BuildStep().CompensateAsync(data, new ReturnSagaContext(), CancellationToken.None);

        await _orderPersistenceService.DidNotReceive()
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _returnRequestPersistenceService.DidNotReceive()
            .UpdateReturnRequestAsync(Arg.Any<Guid>(), Arg.Any<Func<RequestReturn, Task>>(), Arg.Any<CancellationToken>());
    }

    private void SetupUserGateway(Guid customerId, string countryCode = "US", string customerTier = "Standard")
    {
        _userGateway
            .GetUserProfileAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserProfileDto(
                customerId,
                $"{customerId}@test.local",
                "Test Customer",
                countryCode,
                customerTier,
                true)));
    }

    private static ReturnSagaData CreateSampleData(Guid correlationId, Guid? productId = null) => new()
    {
        CorrelationId = correlationId,
        Currency = "USD",
        CustomerId = Guid.NewGuid(),
        RefundAmount = 100m,
        ReturnedItems = new List<OrderItemDto> { new(productId ?? Guid.NewGuid(), 1, 100m, "USD") },
        ReturnReason = "Defective item"
    };

    private static Order CreatePendingOrder()
    {
        var customerId = CustomerId.CreateUnique();
        var address = Address.Create("Baker St", "London", "UK", "NW1");
        var items = new List<OrderItem> { OrderItem.Create(ProductId.CreateUnique(), 1, Money.Create(100, "USD")) };
        return Order.Create(customerId, address, items, "CreditCard");
    }

    private static Order CreateCompleteOrder(DateTime? completedAt = null)
    {
        var order = CreatePendingOrder();
        order.Pay(PaymentId.From("PAY-123"));
        order.Approve();
        order.Complete();
        
        if (completedAt.HasValue)
        {
            // use reflection to set the private _completedAt field for testing
            var field = typeof(Order).GetField("_completedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(order, completedAt.Value);
        }
        
        order.ClearUncommittedEvents();
        return order;
    }

    private static RequestReturn CreateReturnRequest(Order order) =>
        RequestReturn.Create(
            orderId: order.Id,
            customerId: CustomerId.CreateUnique(),
            reason: "Defective",
            itemsToReturn: order.Items.ToList(),
            refundAmount: Money.Create(100, "USD"),
            orderCompletedAt: order.CompletedAt!.Value,
            orderItems: order.Items.ToList(),
            returnWindow: TimeSpan.FromDays(14));
}