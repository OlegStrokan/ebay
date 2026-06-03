namespace Infrastructure.ReadModels;

public class B2BOrderReadModel
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? CompanyId { get; set; }
    public string CompanyName { get; set; } = null!;
    public string Status { get; set; } = null!;

    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = null!;
    public decimal DiscountPercent { get; set; }

    public DateTime? RequestedDeliveryDate { get; set; }
    public Guid? FinalizedOrderId { get; set; }

    public string DeliveryStreet { get; set; } = null!;
    public string DeliveryCity { get; set; } = null!;
    public string DeliveryCountry { get; set; } = null!;
    public string DeliveryPostalCode { get; set; } = null!;

    public string ItemsJson { get; set; } = "[]";
    public string CommentsJson { get; set; } = "[]";

    public DateTime StartedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public int Version { get; set; }

    public DateTime LastSyncedAt { get; set; }
}
