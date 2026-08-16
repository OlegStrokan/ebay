using Application.Common;
using Application.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public static class ApplicationModule
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ApplicationModule).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(ApplicationModule).Assembly);
        services.AddScoped<IOrderCallbackQueueService, OrderCallbackQueueService>();
        services.AddScoped<IMoneyEventQueueService, MoneyEventQueueService>();

        return services;
    }
}