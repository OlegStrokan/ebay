using Domain.Common;
using Domain.Events;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Entities;

// @todo: add condition field like in listing entity
public sealed class Product : AggregateRoot<ProductId>
{
    private string _name = null!;
    private string _description = null!;
    // @think: should we add advance category path? probably yes....in 2.0 version
    private CategoryId _categoryId = null!;
    private Money _price = null!;
    private ProductStatus  _status = null!;
    private SellerId _sellerId = null!;
    private List<ProductAttribute> _attributes = new();
    private List<string> _imageUrls = new();
    private int _stockQuantity;
    private DateTime _createdAt;
    private DateTime? _updatedAt;
    private string? _reviewNotes;

    public string Name => _name;
    public string Description => _description;
    public CategoryId CategoryId => _categoryId;
    public Money Price => _price;
    public ProductStatus Status => _status;
    public SellerId SellerId => _sellerId;
    public int StockQuantity => _stockQuantity;
    public DateTime CreatedAt => _createdAt;
    public DateTime? UpdatedAt => _updatedAt;
    public string? ReviewNotes => _reviewNotes;
    public IReadOnlyList<ProductAttribute> Attributes => _attributes.AsReadOnly();
    public IReadOnlyList<string> ImageUrls  => _imageUrls.AsReadOnly();

    private Product() { }

    public static Product Create(
        SellerId sellerId,
        string name,
        string description,
        CategoryId categoryId,
        Money price,
        int initialStock,
        List<ProductAttribute> attributes,
        List<string> imageUrls)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Product name cannot be empty");

        if (initialStock < 0)
            throw new DomainException("Initial stock cannot be negative");

        var product = new Product
        {
            Id = ProductId.CreateUnique(),
            _sellerId = sellerId,
            _name = name,
            _description = description,
            _categoryId = categoryId,
            _price = price,
            _stockQuantity = initialStock,
            _attributes = attributes ?? [],
            _imageUrls = imageUrls ?? [],
            _status = categoryId.IsPlaceholder() ? ProductStatus.Draft : ProductStatus.PendingApproval,
            _createdAt = DateTime.UtcNow,
        };

        product.AddDomainEvent(new ProductCreatedEvent(
            ProductId: product.Id,
            SellerId: sellerId,
            Name: name,
            Description: description,
            CategoryId: categoryId,
            Price: price,
            InitialStock: initialStock,
            Attributes: product._attributes,
            ImageUrls: product._imageUrls,
            CreatedAt: product._createdAt));

        return product;
    }

    public void Update(
        string name,
        string description,
        CategoryId categoryId,
        Money price,
        List<ProductAttribute> attributes,
        List<string> imageUrls)
    {
        if (_status == ProductStatus.Deleted)
            throw new InvalidProductOperationException(Id.Value, "Cannot update a deleted product");

        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Product name cannot be empty");

        _name = name;
        _description = description;
        _categoryId  = categoryId;
        _price = price;
        _attributes = attributes ?? [];
        _imageUrls = imageUrls ?? [];
        _updatedAt = DateTime.UtcNow;

        if (!_categoryId.IsPlaceholder() && (_status == ProductStatus.Draft || _status == ProductStatus.Rejected))
        {
            _reviewNotes = null;
            ChangeStatus(ProductStatus.PendingApproval);
        }

        AddDomainEvent(new ProductUpdatedEvent(
            ProductId: Id,
            Name: _name,
            Description: _description,
            CategoryId:  _categoryId,
            Price: _price,
            Attributes:  _attributes,
            ImageUrls: _imageUrls,
            UpdatedAt: _updatedAt.Value));
    }

    public void UpdateStock(int newQuantity)
    {
        if (_status == ProductStatus.Deleted)
            throw new InvalidProductOperationException(Id.Value, "Cannot update stock of a deleted product");

        if (newQuantity < 0)
            throw new DomainException("Stock quantity cannot be negative");

        var previousQuantity = _stockQuantity;
        _stockQuantity = newQuantity;
        _updatedAt = DateTime.UtcNow;

        AddDomainEvent(new ProductStockUpdatedEvent(Id, previousQuantity, newQuantity, _updatedAt.Value));

        // i don't want to use switch for 2 cases
        if (newQuantity == 0 && _status == ProductStatus.Approved)
            ChangeStatus(ProductStatus.OutOfStock);
        
        else if (newQuantity > 0 && _status == ProductStatus.OutOfStock)
            ChangeStatus(ProductStatus.Approved);
    }

    public void AdjustStock(int delta)
    {
        if (_status == ProductStatus.Deleted)
            throw new InvalidProductOperationException(Id.Value, "Cannot update stock of a deleted product");

        var newQuantity = Math.Max(0, _stockQuantity + delta);
        var previousQuantity = _stockQuantity;
        _stockQuantity = newQuantity;
        _updatedAt = DateTime.UtcNow;

        AddDomainEvent(new ProductStockUpdatedEvent(Id, previousQuantity, newQuantity, _updatedAt.Value));

        if (newQuantity == 0 && _status == ProductStatus.Approved)
            ChangeStatus(ProductStatus.OutOfStock);

        else if (newQuantity > 0 && _status == ProductStatus.OutOfStock)
            ChangeStatus(ProductStatus.Approved);
    }

    public void Activate()
    {
        _status.ValidateTransitionTo(ProductStatus.Approved);
        ChangeStatus(ProductStatus.Approved);
    }

    public void Deactivate()
    {
        _status.ValidateTransitionTo(ProductStatus.Inactive);
        ChangeStatus(ProductStatus.Inactive);
    }

    public void Delete()
    {
        _status.ValidateTransitionTo(ProductStatus.Deleted);
        ChangeStatus(ProductStatus.Deleted);
        AddDomainEvent(new ProductDeletedEvent(Id, _updatedAt!.Value));
    }

    public void Approve()
    {
        if (_categoryId.IsPlaceholder())
            throw new DomainException("Category must be assigned before approval");

        _status.ValidateTransitionTo(ProductStatus.Approved);
        _reviewNotes = null;
        ChangeStatus(ProductStatus.Approved);
        AddDomainEvent(new ProductApprovedEvent(Id, _updatedAt!.Value));
    }

    public void Reject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Rejection reason cannot be empty");
        _status.ValidateTransitionTo(ProductStatus.Rejected);
        _reviewNotes = reason.Trim();
        ChangeStatus(ProductStatus.Rejected);
        AddDomainEvent(new ProductRejectedEvent(Id, _reviewNotes, _updatedAt!.Value));
    }
    
    
    private void ChangeStatus(ProductStatus newStatus)
    {
        var previous = _status;
        _status = newStatus;
        _updatedAt = DateTime.UtcNow;
        AddDomainEvent(new ProductStatusChangedEvent(Id, previous.Name, newStatus.Name, _updatedAt.Value));
    }
}