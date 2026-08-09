using Application.Sagas;
using Application.Sagas.OrderSaga;
using Application.Sagas.Persistence;
using Application.Interfaces;
using Domain.Common;
using Confluent.Kafka;
using Domain.ValueObjects;
using FluentAssertions;
using Grpc.Core;
using Infrastructure.Extensions;
using Order.E2ETests.Infrastructure;
using Protos.Order;
using System.Text.Json;
using WireMock.ResponseBuilders;
using Xunit;
using Xunit.Abstractions;
using Address = Protos.Order.Address;
using Request = WireMock.RequestBuilders.Request;

namespace Order.E2ETests.Tests;

[Collection("E2E")]
public class CreateOrderE2ETests : IClassFixture<E2ETestServer>, IAsyncLifetime
{
    private readonly E2ETestServer _server;
    private readonly ITestOutputHelper _output;
    private OrderService.OrderServiceClient _client = null!;


    public CreateOrderE2ETests(E2ETestServer testServer, ITestOutputHelper output)
    {
        _server = testServer;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _server.MigrateDatabaseAsync();
        _client = _server.CreateOrderClient();
        _server.ResetAll();
    }

    public Task DisposeAsync() => Task.CompletedTask;


    [Fact]
    public async Task CreateOrder_HappyPath_OrderCompletedSuccessfully()
    {
        _output.WriteLine("Happy Path - Full order flow");
        
        // gprc fakes
        var paymentId = "paymentId";
        var reservationId = "reservationId";

        _server.PaymentService.ProcessShouldSucceed = true;
        _server.PaymentService.PaymentIdToReturn = paymentId;
        _server.InventoryService.ReserveShouldSucceed = true;
        _server.InventoryService.ReservationIdToReturn = reservationId;
        _server.AccountingServer.ShouldSucceed = true;
        _server.ProductService.ShouldSucceed = true;
        _server.ProductService.PriceToReturn = 29.99m; // 2 units × 29.99 = 59.98

        // shipping wire mock - rest
        var shipmentId = "shipmentId";
        var trackingNumber = "trackingNumber";

        _server.ShipmentServer
            .Given(Request.Create().WithPath("/api/shipping/create").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { shipmentId, trackingNumber }));
        var customerId = Guid.NewGuid();
        var request = BuildCreateOrderRequest(customerId);
        
        var response = await _client.CreateOrderAsync(request);

        response.Success.Should().BeTrue("gRPC call must succeed");
        var orderId = Guid.Parse(response.OrderId);
        _output.WriteLine($"Order created: {orderId}");

        // wait for saga to complete before asserting events/read-model
        var saga = await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.Completed, timeoutSeconds: 30);

        saga.Should().NotBeNull("saga must reach Completed within timeout");
        saga!.Status.Should().Be(SagaStatus.Completed);
        _output.WriteLine($"Saga: {saga.Status}");

        // assert event store
        var events = await _server.GetEventsAsync(orderId, AggregateTypes.Order);
        var eventTypes = events.Select(e => e.GetType().Name).ToList();
        _output.WriteLine($"Events: {string.Join(", ", eventTypes)}");

        eventTypes.Should().Contain("OrderCreatedEvent");
        eventTypes.Should().Contain("OrderPaidEvent");
        eventTypes.Should().Contain("OrderTrackingAssignedEvent");
        eventTypes.Should().Contain("OrderCompletedEvent");
    
        // assert read model
        var readModel = await _server.WaitForReadModelStatusAsync(
            orderId, "Completed", timeoutSeconds: 30);

        readModel.Should().NotBeNull();
        readModel!.Status.Should().Be("Completed");
        readModel.CustomerId.Should().Be(customerId);
        readModel.TrackingId.Should().Be(trackingNumber);
        readModel.PaymentId.Should().Be(paymentId);
        _output.WriteLine($"ReadModel: Status={readModel.Status}, Payment={readModel.PaymentId}, Tracking={readModel.TrackingId}");

        // assert grpc calls
        _server.PaymentService.ProcessCalls.Should().HaveCount(1);
        _server.PaymentService.ProcessCalls[0].OrderId.Should().Be(orderId.ToString());
        _server.PaymentService.ProcessCalls[0].Currency.Should().Be("USD");
        _server.PaymentService.ProcessCalls[0].Amount.ToDecimal().Should().BeApproximately(59.98m, 0.01m);

        _server.InventoryService.ReserveCalls.Should().HaveCount(1);
        _server.InventoryService.ReserveCalls[0].OrderId.Should().Be(orderId.ToString());
        _server.InventoryService.ReserveCalls[0].Items.Should().HaveCount(1);

        // product price fetch: one call, one product ID, authoritative price used
        _server.ProductService.GetPricesCalls.Should().HaveCount(1);
        _server.ProductService.GetPricesCalls[0].ProductIds.Should().HaveCount(1, "one distinct product in the order");

        // assert shipping rest calls
        _server.ShipmentServer.LogEntries.Should().ContainSingle(e => e.RequestMessage.Path == "/api/shipping/create");
        
        // assert email event on kafka. Email is not mocked - we only verify
        // OrderCompletedEvent reached Kafka. external system consumes from this same topic

        var emailEventOnKafka = await WasEventPublishedtoKafkaAsync(
            orderId, "OrderCompletedEvent");

        emailEventOnKafka.Should().BeTrue(
            "OrderCompletedEvent must be on Kafka so external email consumer can pick it up");

        _output.WriteLine("OrderCompletedEvent published to Kafka");
        
        _output.WriteLine("Passed: Full order flow end-to-end type shit");

    }

    [Fact]
    public async Task CreateOrder_PaymentDeadlineExceeded_SagaWaitsWithoutCompensation()
    {
        _output.WriteLine("Payment timeout => uncertain waiting state");

        _server.InventoryService.ReserveShouldSucceed = true;
        _server.InventoryService.ReservationIdToReturn = "reservation-timeout";
        _server.ProductService.ShouldSucceed = true;

        _server.PaymentService.ProcessRpcStatusCodeToThrow = StatusCode.DeadlineExceeded;
        _server.PaymentService.ProcessRpcErrorDetailToThrow = "simulated payment timeout";

        var request = BuildCreateOrderRequest(Guid.NewGuid());
        var response = await _client.CreateOrderAsync(request);

        response.Success.Should().BeTrue("order command should be accepted");
        var orderId = Guid.Parse(response.OrderId);

        var saga = await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.WaitingForEvent, timeoutSeconds: 30);

        saga.Should().NotBeNull("saga should pause while payment status is uncertain");

        var steps = await _server.GetSagaStepLogsAsync(saga!.Id);
        steps.Single(s => s.StepName == OrderSagaSteps.ReserveInventory).Status.Should().Be(StepStatus.Completed);
        steps.Single(s => s.StepName == OrderSagaSteps.AuthorizePayment).Status.Should().Be(StepStatus.Waiting);

        _server.InventoryService.ReleaseCalls.Should().BeEmpty(
            "timeout should not trigger compensation/refund flow immediately");

        var context = JsonSerializer.Deserialize<OrderSagaContext>(saga.Context);
        context.Should().NotBeNull();
        context!.PaymentStatus.Should().Be(OrderSagaPaymentStatus.Uncertain);

        _output.WriteLine("PASSED: timeout correctly mapped to uncertain waiting state");
    }

    [Fact]
    public async Task CreateOrder_PaymentServiceUnavailable_SagaCompensatesAndCancelsOrder()
    {
        _output.WriteLine("Payment service unavailable => compensation");

        _server.InventoryService.ReserveShouldSucceed = true;
        _server.InventoryService.ReservationIdToReturn = "reservation-unavailable";
        _server.InventoryService.ReleaseShouldSucceed = true;
        _server.ProductService.ShouldSucceed = true;

        _server.PaymentService.ProcessRpcStatusCodeToThrow = StatusCode.Unavailable;
        _server.PaymentService.ProcessRpcErrorDetailToThrow = "simulated payment service down";

        var request = BuildCreateOrderRequest(Guid.NewGuid());
        var response = await _client.CreateOrderAsync(request);

        response.Success.Should().BeTrue("order command should be accepted");
        var orderId = Guid.Parse(response.OrderId);

        var saga = await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.Compensated, timeoutSeconds: 30);

        saga.Should().NotBeNull("unavailable payment service should fail payment step and compensate");

        var steps = await _server.GetSagaStepLogsAsync(saga!.Id);
        steps.Single(s => s.StepName == OrderSagaSteps.AuthorizePayment).Status.Should().Be(StepStatus.Failed);
        steps.Single(s => s.StepName == OrderSagaSteps.ReserveInventory).Status.Should().Be(StepStatus.Compensated);

        _server.InventoryService.ReleaseCalls.Should().HaveCountGreaterOrEqualTo(1);

        var events = await _server.GetEventsAsync(orderId, AggregateTypes.Order);
        events.Select(e => e.GetType().Name).Should().Contain("OrderCancelledEvent");

        _output.WriteLine("PASSED: unavailable payment correctly triggers compensation flow");
    }

    [Fact]
    public async Task CreateOrder_Idempotency_DuplicateRequestSameOrderId()
    {
        _output.WriteLine("Idempotency - Duplicate request");

        _server.PaymentService.ProcessShouldSucceed = true;
        _server.InventoryService.ReserveShouldSucceed = true;
        _server.ProductService.ShouldSucceed = true;

        _server.ShipmentServer
            .Given(Request.Create().WithPath("/api/shipping/create").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { shipmentId = "ShipmentId", trackingNumber = "TrackingNumber" }));
        
        // same key on both request - simulated client retry after network issue

        var idempotencyKey = $"idem-{Guid.NewGuid()}";
        var request = BuildCreateOrderRequest(Guid.NewGuid(), idempotencyKey: idempotencyKey);
        
        _output.WriteLine("Request #1...");
        var response1 = await _client.CreateOrderAsync(request);

        _output.WriteLine("Request #2 (same idempotency key)...");
        var response2 = await _client.CreateOrderAsync(request);

        response1.Success.Should().BeTrue();
        response2.Success.Should().BeTrue();
        response1.OrderId.Should().Be(response2.OrderId, "same key must return same order ID");
        
        _output.WriteLine($"Both returned: {response1.OrderId}");

        var orderId = Guid.Parse(response1.OrderId);

        // wait for saga to run payment before asserting call counts
        await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.Completed, timeoutSeconds: 30);

        var events = await _server.GetEventsAsync(orderId, AggregateTypes.Order);

        events.Count(e => e.GetType().Name == "OrderCreatedEvent")
            .Should().Be(1, "only ONE order must be created");

        _server.PaymentService.ProcessCalls.Should().HaveCount(1,
            "payment changed exactly once");
        
        _output.WriteLine("PASSED: Idempotency works!");
    }

    [Fact]
    public async Task CreateOrder_ShouldReject_WhenCurrentRegionIsNotOwner()
    {
        _output.WriteLine("Region ownership guard - reject non-owner region");

        _server.SetWriteOwnershipOverride(_ => new WriteRegionOwnershipDecision(
            IsEnabled: true,
            IsCurrentRegionOwner: false,
            CurrentRegion: "eu-west-1",
            OwnerRegion: "us-east-1"));

        var request = BuildCreateOrderRequest(Guid.NewGuid());
        var response = await _client.CreateOrderAsync(request);

        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Contain("Write ownership mismatch");
        response.ErrorMessage.Should().Contain("us-east-1");

        _server.PaymentService.ProcessCalls.Should().BeEmpty();
        _server.InventoryService.ReserveCalls.Should().BeEmpty();

        _output.WriteLine("PASSED: non-owner region rejected before side effects");
    }


    [Fact]
    public async Task CreateOrder_PaymentDeclined_SagaCompensatesAndCancelsOrder()
    {
        _output.WriteLine("Payment declined => compensation");

        _server.InventoryService.ReserveShouldSucceed = true;
        _server.InventoryService.ReservationIdToReturn = "ReservationId";
        _server.InventoryService.ReleaseShouldSucceed = true;

        _server.PaymentService.ProcessShouldSucceed = false;
        _server.PaymentService.ErrorCode = "PAYMENT_DECLINED";
        _server.PaymentService.ErrorMessage = "Card declined by issuer";

        _server.ProductService.ShouldSucceed = true;

        var request = BuildCreateOrderRequest(Guid.NewGuid());
        var response = await _client.CreateOrderAsync(request);

        response.Success.Should().BeTrue("gRPC accepts the order; payment is async");
        var orderId = Guid.Parse(response.OrderId);
        _output.WriteLine("Order accepted: {orderId}. Waiting for compensation...");

        await Task.Delay(TimeSpan.FromSeconds(10));
        
        // saga should be Compensated
        var saga = await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.Compensated, timeoutSeconds: 30);

        saga.Should().NotBeNull();
        saga!.Status.Should().Be(SagaStatus.Compensated);
        _output.WriteLine($"Saga: {saga.Status}");

        var steps = await _server.GetSagaStepLogsAsync(saga.Id);

        steps.Single(s => s.StepName == OrderSagaSteps.AuthorizePayment)
            .Status.Should().Be(StepStatus.Failed);

        steps.Single(s => s.StepName == OrderSagaSteps.ReserveInventory)
            .Status.Should().Be(StepStatus.Compensated,
                "inventory must be released when payment fails");
        
        _output.WriteLine("AuthorizePayment=Failed, ReserveInventory=Compensated");

        _server.InventoryService.ReleaseCalls.Should().HaveCountGreaterOrEqualTo(1);
        _server.InventoryService.ReleaseCalls[0].ReservationId.Should().Be("ReservationId");

        var events = await _server.GetEventsAsync(orderId, AggregateTypes.Order);
        var eventTypes = events.Select(e => e.GetType().Name).ToList();

        eventTypes.Should().Contain("OrderCreatedEvent");
        eventTypes.Should().Contain("OrderCancelledEvent");
        eventTypes.Should().NotContain("OrderPaidEvent");

        var order = Domain.Entities.Order.Order.FromHistory(events);
        order.Status.Should().Be(OrderStatus.Cancelled);
        
        _output.WriteLine($"Order status: {order.Status}");
        _output.WriteLine("PASSED: Compensation worked!");
    }

    [Fact(Skip = "Manual: kill SagaOrchestrationService mid-run then restart to trigger recovery")]
    public async Task CreateOrder_SagaCrash_ResumesWithoutDuplicateSteps()
    {
        _output.WriteLine("Disaster Recovery");

        _server.PaymentService.ProcessShouldSucceed = true;
        _server.InventoryService.ReserveShouldSucceed = true;
        _server.ProductService.ShouldSucceed = true;

        _server.ShipmentServer
            .Given(Request.Create().WithBodyAsJson("/api/shipping/create").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { shipmentId = "ShipmentId", trackingNumber = "TrackingNumber " }));

        var response = await _client.CreateOrderAsync(BuildCreateOrderRequest(Guid.NewGuid()));
        var orderId = Guid.Parse(response.OrderId);
        
        _output.WriteLine($"Order {orderId} created. Saga is runnin...");
        _output.WriteLine("Kill SagaOrchestrationService container NOW, restart after 5s");

        await Task.Delay(TimeSpan.FromSeconds(20));

        var saga = await _server.WaitForSagaStatusAsync(
            orderId, SagaTypes.OrderSaga, SagaStatus.Completed, timeoutSeconds: 60);

        saga.Should().NotBeNull();

        saga!.Status.Should().Be(SagaStatus.Completed);

        var steps = await _server.GetSagaStepLogsAsync(saga.Id);

        steps.Select(s => s.StepName).Should().OnlyHaveUniqueItems(
            "each step must run exactly once - no duplicates after crash + resume");
        
        _output.WriteLine("Passed: saga resumed without duplicates");
    }

    private static CreateOrderRequest BuildCreateOrderRequest(
        Guid customerId,
        string? idempotencyKey = null)
    {
        var req = new CreateOrderRequest
        {
            CustomerId = customerId.ToString(),
            PaymentMethod = "CreditCard",
            IdempotencyKey = idempotencyKey ?? $"test-{Guid.NewGuid()}",
            DeliveryAddress = new Address
            {
                Street = "123 Main Shit",
                City = "Kabul",
                Country = "Talibastan",
                PostalCode = "11092001"
            }
        };
        
        req.Items.Add(new OrderItem
        {
            ProductId = Guid.NewGuid().ToString(),
            Quantity = 2,
            Price = 29.99m.ToDecimalValue(),
            Currency = "USD"
        });

        return req;
    }

    private async Task<bool> WasEventPublishedtoKafkaAsync(
        Guid orderId, string eventType, int timeoutSeconds = 15)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _server.KafkaBootstrapServers,
            GroupId = $"e2e-verify-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();

        consumer.Subscribe("order.events");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var msg = consumer.Consume(TimeSpan.FromSeconds(1));
                if (msg?.Message?.Value is null) continue;

                if (msg.Message.Value.Contains(orderId.ToString()) &&
                    msg.Message.Value.Contains(eventType))
                    return true;
            }
        }

        finally
        {
            consumer.Unsubscribe();
        }

        return false;
    }
}