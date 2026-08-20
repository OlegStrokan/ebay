namespace Infrastructure.Options;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";
    public string MoneyEventsTopic { get; init; } = "payment.money-events";
}
