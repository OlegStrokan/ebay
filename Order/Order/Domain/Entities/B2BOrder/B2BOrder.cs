using Domain.Common;
using Domain.Events.B2BOrder;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Entities.B2BOrder;

// B2B "Living" Quote - long-lived aggregate, negotiated over days/weeks/months
// Event volume can be above average size => snapshot threshold is intentionally LOW (20 events)
// Each "Save Draft" click in the UI can emit dozens of events (one per changed line item)

public sealed class B2BOrder : AggregateRoot<B2BOrderId>
{
    private CustomerId _customerId;
    private string _companyName = string.Empty;
    private Guid? _companyId;
    private B2BOrderStatus _status = null!;
    private Address _deliveryAddress = null!;
    private decimal _discountPercent;
    private DateTime? _requestedDeliveryDate;
    private Guid? _finalizedOrderId;
    private List<QuoteLineItem> _items = new();
    private List<string> _comments = new();
    private DateTime _createdAt;
    private DateTime? _updatedAt;

    public CustomerId CustomerId => _customerId;
    public string CompanyName => _companyName;
    public Guid? CompanyId => _companyId;
    public B2BOrderStatus Status => _status;
    public Address DeliveryAddress => _deliveryAddress;
    public decimal DiscountPercent => _discountPercent;
    public DateTime? RequestedDeliveryDate => _requestedDeliveryDate;
    public Guid? FinalizedOrderId => _finalizedOrderId;
    public IReadOnlyList<string> Comments => _comments.AsReadOnly();

    // only non-removed items exposed; IsRemoved items stay in the list for event replay correctness
    public IReadOnlyList<QuoteLineItem> ActiveItems =>
        _items.Where(i => !i.IsRemoved).ToList().AsReadOnly();

    public Money TotalPrice
    {
        get
        {
            var active = _items.Where(i => !i.IsRemoved).ToList();
            if (!active.Any()) return Money.Default("USD");

            var currency = active[0].EffectiveUnitPrice.Currency;
            var subtotal = active.Aggregate(
                Money.Default(currency),
                (acc, item) => acc.Add(item.LineTotal));

            return _discountPercent > 0
                ? Money.Create(Math.Round(subtotal.Amount * (1 - _discountPercent / 100m), 2), currency)
                : subtotal;
        }
    }

    private B2BOrder() { }

    private B2BOrder(B2BOrderSnapshotState state)
    {
        Id = B2BOrderId.From(state.Id);
        _customerId = CustomerId.From(state.CustomerId);
        _companyName = state.CompanyName;
        _companyId = state.CompanyId;
        _status = B2BOrderStatus.FromName(state.Status);
        _deliveryAddress = Address.Create(state.Street, state.City, state.Country, state.PostalCode);
        _discountPercent = state.DiscountPercent;
        _requestedDeliveryDate = state.RequestedDeliveryDate;
        _finalizedOrderId = state.FinalizedOrderId;
        _items = state.Items.Select(QuoteLineItem.FromSnapshot).ToList();
        _comments = state.Comments.ToList();
        _createdAt = state.CreatedAt;
        _updatedAt = state.UpdatedAt;
        RestoreVersion(state.Version);
    }

    public B2BOrderSnapshotState ToSnapshotState() => new(
        Id: Id.Value,
        CustomerId: _customerId.Value,
        CompanyName: _companyName,
        Status: _status.Name,
        DiscountPercent: _discountPercent,
        Street: _deliveryAddress.Street,
        City: _deliveryAddress.City,
        Country: _deliveryAddress.Country,
        PostalCode: _deliveryAddress.PostalCode,
        RequestedDeliveryDate: _requestedDeliveryDate,
        FinalizedOrderId: _finalizedOrderId,
        Version: Version,
        CreatedAt: _createdAt,
        UpdatedAt: _updatedAt,
        Items: _items.Select(i => i.ToSnapshotState()).ToList(),
        Comments: _comments.ToList(),
        CompanyId: _companyId);

    public static B2BOrder FromSnapshot(B2BOrderSnapshotState state) => new(state);
    
    public static B2BOrder Start(CustomerId customerId, string companyName, Address deliveryAddress, Guid? companyId = null)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            throw new DomainException("Company name is required");

        var order = new B2BOrder();
        var evt = new B2BOrderStartedEvent(
            B2BOrderId.CreateUnique(), customerId, companyName, deliveryAddress, DateTime.UtcNow, companyId);
        order.RaiseEvent(evt);
        return order;
    }

    // One call per product - caller batches these in UpdateQuoteDraft command
    public void AddItem(ProductId productId, int quantity, Money unitPrice)
    {
        EnsureInDraft();

        if (_items.Any(i => i.ProductId == productId && !i.IsRemoved))
            throw new DomainException(
                $"Product {productId} already exists. Use ChangeItemQuantity to update it.");

        var evt = new LineItemAddedEvent(
            Id, QuoteLineItemId.CreateUnique(), productId, quantity, unitPrice, DateTime.UtcNow);
        RaiseEvent(evt);
    }

    public void RemoveItem(ProductId productId)
    {
        EnsureInDraft();
        var item = FindActiveItem(productId);
        RaiseEvent(new LineItemRemovedEvent(Id, item.Id, DateTime.UtcNow));
    }

    public void ChangeItemQuantity(ProductId productId, int newQuantity)
    {
        EnsureInDraft();
        if (newQuantity <= 0)
            throw new DomainException("Quantity must be greater than zero");

        var item = FindActiveItem(productId);
        RaiseEvent(new LineItemQuantityChangedEvent(Id, item.Id, newQuantity, DateTime.UtcNow));
    }

    // B2B-specific: saleshead manually overrides a line-item unit price
    public void AdjustItemPrice(ProductId productId, Money newPrice)
    {
        EnsureInDraft();
        var item = FindActiveItem(productId);
        RaiseEvent(new LineItemPriceAdjustedEvent(Id, item.Id, newPrice, DateTime.UtcNow));
    }

    public void ApplyDiscount(decimal discountPercent)
    {
        EnsureInDraft();
        if (discountPercent is < 0 or > 100)
            throw new DomainException("Discount must be between 0 and 100");

        RaiseEvent(new DiscountAppliedEvent(Id, discountPercent, DateTime.UtcNow));
    }

    // Negotiation log = both sides can comment without changing state
    public void AddComment(string author, string text)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new DomainException("Author is required");
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Comment text cannot be empty");

        RaiseEvent(new CommentAddedEvent(Id, author, text, DateTime.UtcNow));
    }

    public void ChangeDeliveryDate(DateTime? deliveryDate)
    {
        EnsureInDraft();
        RaiseEvent(new DeliveryDateChangedEvent(Id, deliveryDate, DateTime.UtcNow));
    }

    public void ChangeDeliveryAddress(Address address)
    {
        EnsureInDraft();
        RaiseEvent(new DeliveryAddressChangedEvent(Id, address, DateTime.UtcNow));
    }

    // Seals the quote - the command handler will create a normal B2C Order and pass its id here
    public void Finalize(Guid createdOrderId)
    {
        EnsureInDraft();
        if (_items.All(i => i.IsRemoved))
            throw new DomainException("Cannot finalize an empty quote");

        RaiseEvent(new QuoteFinalizedEvent(Id, createdOrderId, DateTime.UtcNow));
    }

    public void Cancel(List<string> reasons)
    {
        if (_status == B2BOrderStatus.Finalized)
            throw new DomainException("Cannot cancel a finalized quote");
        if (_status == B2BOrderStatus.Cancelled)
            throw new DomainException("Quote is already cancelled");

        RaiseEvent(new B2BOrderCancelledEvent(Id, reasons, DateTime.UtcNow));
    }

    // event projection

    private void Apply(B2BOrderStartedEvent evt)
    {
        Id = evt.B2BOrderId;
        _customerId = evt.CustomerId;
        _companyName = evt.CompanyName;
        _companyId = evt.CompanyId;
        _deliveryAddress = evt.DeliveryAddress;
        _status = B2BOrderStatus.Draft;
        _discountPercent = 0;
        _createdAt = evt.OccurredAt;
    }

    private void Apply(LineItemAddedEvent evt)
    {
        _items.Add(QuoteLineItem.Restore(evt.LineItemId, evt.ProductId, evt.Quantity, evt.UnitPrice));
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(LineItemRemovedEvent evt)
    {
        _items.First(i => i.Id == evt.LineItemId).Remove();
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(LineItemQuantityChangedEvent evt)
    {
        _items.First(i => i.Id == evt.LineItemId).ChangeQuantity(evt.NewQuantity);
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(LineItemPriceAdjustedEvent evt)
    {
        _items.First(i => i.Id == evt.LineItemId).AdjustPrice(evt.NewPrice);
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(DiscountAppliedEvent evt)
    {
        _discountPercent = evt.DiscountPercent;
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(CommentAddedEvent evt)
    {
        _comments.Add($"[{evt.Author} @ {evt.OccurredAt:u}] {evt.Text}");
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(DeliveryDateChangedEvent evt)
    {
        _requestedDeliveryDate = evt.NewDeliveryDate;
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(DeliveryAddressChangedEvent evt)
    {
        _deliveryAddress = evt.NewAddress;
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(QuoteFinalizedEvent evt)
    {
        _status = B2BOrderStatus.Finalized;
        _finalizedOrderId = evt.FinalizedOrderId;
        _updatedAt = evt.OccurredAt;
    }

    private void Apply(B2BOrderCancelledEvent evt)
    {
        _status = B2BOrderStatus.Cancelled;
        _updatedAt = evt.OccurredAt;
    }

    // helpers 

    private void EnsureInDraft()
    {
        if (_status != B2BOrderStatus.Draft)
            throw new DomainException(
                $"Operation not allowed: quote is '{_status}', expected 'Draft'");
    }

    private QuoteLineItem FindActiveItem(ProductId productId)
        => _items.FirstOrDefault(i => i.ProductId == productId && !i.IsRemoved)
           ?? throw new DomainException($"Active item for product '{productId}' not found in quote");

    public static B2BOrder FromHistory(IEnumerable<IDomainEvent> history)
    {
        var order = new B2BOrder();
        order.LoadFromHistory(history);
        return order;
    }
}
