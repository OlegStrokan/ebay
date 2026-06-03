using Application.Sagas.Handlers;
using Application.Sagas.OrderSaga;
using Application.Sagas.OrderSaga.Steps;
using Application.Sagas.ReturnSaga;
using Application.Sagas.ReturnSaga.Steps;
using Application.Sagas.Steps;
using Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;

namespace Application;

public static class ApplicationModule
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // domain services (registered here because application => domain reference already exists)
        services.AddSingleton<ReturnPolicyService>();
        
        // command/query handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationModule).Assembly));
        
        // validators
        services.AddValidatorsFromAssembly(typeof(ApplicationModule).Assembly);
        
        // sagas
        services.AddScoped<IOrderSaga, OrderSaga>();
        services.AddScoped<IReturnSaga, ReturnSaga>();
        
        // order saga steps
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, CancelOrderOnFailureStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, ReserveInventoryStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, CapturePaymentStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, AwaitPaymentConfirmationStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, UpdateOrderStatusStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, CreateShipmentStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, CompleteOrderStep>();
        services.AddScoped<ISagaStep<OrderSagaData, OrderSagaContext>, SendConfirmationEmailStep>();
        
        // return saga step
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, ValidateReturnRequestStep>();
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, AwaitReturnShipmentStep>();
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, ConfirmReturnReceivedStep>();
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, CompleteReturnStep>();
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, ProcessRefundStep>();
        services.AddScoped<ISagaStep<ReturnSagaData, ReturnSagaContext>, UpdateAccountingRecordsStep>();

        // saga event handlers - consumed by kafka consumer
        // registered both as interface (for GetServices<ISagaEventHandler>) and as concrete
        // type (for SagaHandlerFactory.GetHandler which resolves by concrete type directly)
        services.AddScoped<OrderCreatedEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<OrderCreatedEventHandler>());

        services.AddScoped<ReturnRequestCreatedEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<ReturnRequestCreatedEventHandler>());

        services.AddScoped<ReturnShipmentDeliveredEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<ReturnShipmentDeliveredEventHandler>());

        services.AddScoped<PaymentSucceededEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<PaymentSucceededEventHandler>());

        services.AddScoped<PaymentFailedEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<PaymentFailedEventHandler>());

        services.AddScoped<RefundSucceededEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<RefundSucceededEventHandler>());

        services.AddScoped<RefundFailedEventHandler>();
        services.AddScoped<ISagaEventHandler>(sp => sp.GetRequiredService<RefundFailedEventHandler>());

        // lightweight type-only descriptors used by SagaHandlerFactory (singleton).
        // these carry just EventType string + handler .NET Type - no DI chains involved.
        services.AddSingleton(new SagaHandlerDescriptor("OrderCreatedEvent",typeof(OrderCreatedEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("ReturnRequestCreatedEvent",typeof(ReturnRequestCreatedEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("ReturnShipmentDeliveredEvent",typeof(ReturnShipmentDeliveredEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("PaymentSucceededEvent",typeof(PaymentSucceededEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("PaymentFailedEvent",typeof(PaymentFailedEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("RefundSucceededEvent",typeof(RefundSucceededEventHandler)));
        services.AddSingleton(new SagaHandlerDescriptor("RefundFailedEvent",typeof(RefundFailedEventHandler)));

        return services;
    }
}