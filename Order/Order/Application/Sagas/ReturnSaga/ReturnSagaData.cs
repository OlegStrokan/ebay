using Application.Common.Enums;
using Application.DTOs;

namespace Application.Sagas.ReturnSaga;

public class ReturnSagaData : SagaData
{
    // CorrelationId is the OrderId; an order can have several returns, so the accounting
    // reversal needs this to stay idempotent
    public Guid ReturnRequestId { get; set; }
    public Guid CustomerId { get; set; }
    public string ReturnReason { get; set; } = string.Empty;
    public List<OrderItemDto> ReturnedItems { get; set; } = new();
    public decimal RefundAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public ShippingCarrier ShippingCarrier { get; set; } = ShippingCarrier.Dpd;
}