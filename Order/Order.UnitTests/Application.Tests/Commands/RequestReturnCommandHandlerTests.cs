using Application.Commands.RequestReturn;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Domain.Entities.Order;
using Domain.Entities.RequestReturn;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Application.Tests.Commands;

public class RequestReturnCommandHandlerTest
{

    private readonly IOrderPersistenceService _orderPersistenceService =
        Substitute.For<IOrderPersistenceService>();

    private readonly IIdempotencyRepository _idempotencyRepository =
        Substitute.For<IIdempotencyRepository>();

    private readonly IReturnRequestPersistenceService _returnRequestPersistenceService =
        Substitute.For<IReturnRequestPersistenceService>();

    private readonly IUserGateway _userGateway =
        Substitute.For<IUserGateway>();

    private readonly ReturnPolicyService _returnPolicyService = new();
    private readonly ILogger<RequestReturnCommandHandler> _logger =
        Substitute.For<ILogger<RequestReturnCommandHandler>>();

    private RequestReturnCommandHandler BuildHandler() =>
        new(_orderPersistenceService, _idempotencyRepository, _returnRequestPersistenceService, _userGateway, _returnPolicyService, _logger);

    private void SetupUserGateway(Guid customerId)
    {
        _userGateway
            .GetUserProfileAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserProfileDto(
                customerId,
                $"{customerId}@test.local",
                "Return Customer",
                "US",
                "Standard",
                true)));
    }

    // -------------------------------------------------------------------------

    private static Order CreateCompletedOrder()
    {
        var items = new List<OrderItem>
        {
            OrderItem.Create(ProductId.CreateUnique(), 1, Money.Create(100, "USD"))
        };
        var order = Order.Create(
            CustomerId.CreateUnique(),
            Address.Create("Baker St", "London", "UK", "NW1"),
            items,
            "CreditCard");
        order.Pay(PaymentId.From("pay_123"));
        order.Approve();
        order.Complete();
        order.ClearUncommittedEvents();
        return order;
    }

    [Fact]
    public async Task Handle_ShouldReturnSuccess_WhenReturnIsRequestedSuccessfully()
    {
        var order = CreateCompletedOrder();
        var returnRequestId = Guid.NewGuid();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        _returnRequestPersistenceService
            .CreateReturnRequestAsync(
                Arg.Any<RequestReturn>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(returnRequestId);

        var result = await BuildHandler().Handle(
            CreateValidCommand(order.Id.Value, order.Items.First().ProductId.Value),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        await _returnRequestPersistenceService.Received(1).CreateReturnRequestAsync(
            Arg.Any<RequestReturn>(),
            Arg.Is<string?>(k => k == "idempotency-key"),
            Arg.Any<Guid?>(),  // handler passes returnRequest.Id.Value, not order.Id.Value
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnExistingId_WhenDuplicateIdempotencyKey()
    {
        var existingId = Guid.NewGuid();
        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdempotencyRecord("idempotency-key", existingId, DateTime.UtcNow));

        var result = await BuildHandler().Handle(
            CreateValidCommand(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existingId, result.Value);

        // persistence must NOT be called
        await _returnRequestPersistenceService.DidNotReceive().CreateReturnRequestAsync(
            Arg.Any<RequestReturn>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenOrderNotFound()
    {
        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await BuildHandler().Handle(
            CreateValidCommand(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenOrderIsNotCompleted()
    {
        // Order is Pending - not eligible for return
        var items = new List<OrderItem>
        {
            OrderItem.Create(ProductId.CreateUnique(), 1, Money.Create(100, "USD"))
        };
        var pendingOrder = Order.Create(
            CustomerId.CreateUnique(),
            Address.Create("S", "C", "C", "P"),
            items,
            "CreditCard");

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(pendingOrder);

        SetupUserGateway(pendingOrder.CustomerId.Value);

        var result = await BuildHandler().Handle(
            CreateValidCommand(pendingOrder.Id.Value, items.First().ProductId.Value),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("must be completed", result.Error);
    }

    [Fact]
    public async Task Handle_ShouldReturnFailure_WhenPersistenceThrows()
    {
        var order = CreateCompletedOrder();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        _returnRequestPersistenceService
            .CreateReturnRequestAsync(
                Arg.Any<RequestReturn>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("Persistence failure"));

        var result = await BuildHandler().Handle(
            CreateValidCommand(order.Id.Value, order.Items.First().ProductId.Value),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Persistence failure", result.Error);
    }

    // -------------------------------------------------------------------------

    private static RequestReturnCommand CreateValidCommand(Guid orderId, Guid productId) =>
        new(
            orderId,
            "Item arrived broken",
            new List<OrderItemDto> { new(productId, 1, 100, "USD") },
            "idempotency-key");

    [Fact]
    public async Task Handle_ShouldUseOriginalPrice_WhenClientSendsInflatedPrice()
    {
        var order = CreateCompletedOrder();
        var originalItem = order.Items.First();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        _returnRequestPersistenceService
            .CreateReturnRequestAsync(
                Arg.Any<RequestReturn>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        // Client sends inflated price of 99999.99
        var fraudCommand = new RequestReturnCommand(
            order.Id.Value,
            "Return",
            new List<OrderItemDto> { new(originalItem.ProductId.Value, 1, 99999.99m, "USD") },
            "idempotency-key");

        var result = await BuildHandler().Handle(fraudCommand, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify the return request was created with the original price, not the inflated one
        await _returnRequestPersistenceService.Received(1).CreateReturnRequestAsync(
            Arg.Is<RequestReturn>(rr =>
                rr.RefundAmount.Amount == originalItem.PriceAtPurchase.Amount),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldReject_WhenQuantityExceedsOriginalOrder()
    {
        var order = CreateCompletedOrder(); // has 1 item with quantity 1
        var originalItem = order.Items.First();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        var fraudCommand = new RequestReturnCommand(
            order.Id.Value,
            "Return",
            new List<OrderItemDto> { new(originalItem.ProductId.Value, 1000, 100, "USD") },
            "idempotency-key");

        var result = await BuildHandler().Handle(fraudCommand, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Maximum returnable quantity", result.Error);
    }

    [Fact]
    public async Task Handle_ShouldReject_WhenProductNotInOriginalOrder()
    {
        var order = CreateCompletedOrder();
        var fakeProductId = Guid.NewGuid();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((RequestReturn?)null);

        var fraudCommand = new RequestReturnCommand(
            order.Id.Value,
            "Return",
            new List<OrderItemDto> { new(fakeProductId, 1, 100, "USD") },
            "idempotency-key");

        var result = await BuildHandler().Handle(fraudCommand, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("is not part of order", result.Error);
    }

    [Fact]
    public async Task Handle_ShouldReject_WhenOrderAlreadyHasReturnRequest()
    {
        var order = CreateCompletedOrder();

        _idempotencyRepository
            .GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdempotencyRecord?)null);

        _orderPersistenceService
            .LoadOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(order);

        SetupUserGateway(order.CustomerId.Value);

        var existingReturn = RequestReturn.Create(
            order.Id,
            order.CustomerId,
            "Already returned",
            order.Items.ToList(),
            Money.Create(100, "USD"),
            DateTime.UtcNow.AddDays(-1),
            order.Items.ToList(),
            TimeSpan.FromDays(30));

        _returnRequestPersistenceService
            .LoadByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(existingReturn);

        var result = await BuildHandler().Handle(
            CreateValidCommand(order.Id.Value, order.Items.First().ProductId.Value),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("already has return request", result.Error);

        // Must not reach the transaction that would trip PK_ReturnRequestLookups.
        await _returnRequestPersistenceService.DidNotReceive().CreateReturnRequestAsync(
            Arg.Any<RequestReturn>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }
}