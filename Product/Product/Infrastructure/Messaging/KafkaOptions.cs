namespace Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string ProductEventsTopic { get; set; } = "product.events";
    public string InventoryEventsTopic { get; set; } = "inventory.events";
    public string InventoryConsumerGroupId { get; set; } = "product-service-inventory";
    public string InventoryDlqTopic { get; set; } = "product.inventory.dlq";
}
