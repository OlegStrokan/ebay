namespace Infrastructure.Messaging;


public class KafkaOptions
{
    public const string SectionName = "Kafka";
    
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string ConsumerGroupId { get; set; } = "order-service";
    // SagaOrchestrationService and KafkaReadModelSynchronizer both consume "order.events"
    // This is intentional - they use different consumer group id (see ConsumerGroupId below and
    // KafkaReadModelSynchronizer's hardcoded "read-model-updater" group).
    // never set both services to the same GroupId or they will split partitions between them.
    public string SagaTopic { get; set; } = "order.events";
    public string OrderEventsTopic { get; set; } = "order.events";
    public string EmailEventsTopic { get; set; } = "email.events";
    public string ReturnEventsTopic { get; set; } = "return.events";
    public string InventoryEventsTopic { get; set; } = "inventory.events";
    public bool EnableAutoCommit { get; set; } = false;
}