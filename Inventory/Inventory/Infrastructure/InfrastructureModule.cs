using Application.Interfaces;
using Confluent.Kafka;
using Infrastructure.BackgroundServices;
using Infrastructure.Messaging;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure;

public static class InfrastructureModule
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.Configure<ReservationExpiryOptions>(configuration.GetSection(ReservationExpiryOptions.SectionName));

        services.AddDbContextFactory<InventoryDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")));

        services.AddScoped<IInventoryReservationStore, InventoryReservationStore>();
        services.AddSingleton<InventoryDbInitializer>();

        services.AddSingleton<IProducer<string, string>>(sp =>
        {
            var kafkaOptions = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = kafkaOptions.BootstrapServers,
                ClientId = kafkaOptions.ClientId
            };

            return new ProducerBuilder<string, string>(producerConfig).Build();
        });

        services.AddSingleton<IOutboxPublisher, KafkaOutboxPublisher>();
        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<ReservationExpiryProcessor>();

        return services;
    }
}
