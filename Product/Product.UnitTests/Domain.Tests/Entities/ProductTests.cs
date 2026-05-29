using Domain.Entities;
using Domain.Events;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Tests.Entities;

[TestFixture]
public class ProductTests
{
    private SellerId _sellerId;
    private CategoryId _categoryId;
    private Money _price;
    private List<ProductAttribute> _attributes;
    private List<string> _imageUrls;

    [SetUp]
    public void SetUp()
    {
        _sellerId = SellerId.CreateUnique();
        _categoryId = CategoryId.CreateUnique();
        _price = Money.Create(99.99m, "USD");
        _attributes = [new ProductAttribute("Color", "Red"), new ProductAttribute("Size", "XL")];
        _imageUrls  = ["https://example.com/img1.jpg", "https://example.com/img2.jpg"];
    }

    private Product CreateProduct(int stock = 10) =>
        Product.Create(_sellerId, "Test Product", "A great product", _categoryId, _price, stock, _attributes, _imageUrls);

    private Product CreateActiveProduct(int stock = 10)
    {
        var product = CreateProduct(stock);
        product.ClearDomainEvents();
        product.Approve();
        product.ClearDomainEvents();
        return product;
    }

    #region Create

    [Test]
    public void Create_ShouldReturnProductWithPendingReviewStatus()
    {
        var product = CreateProduct();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.PendingReview));
    }

    [Test]
    public void Create_ShouldSetAllProperties()
    {
        var product = CreateProduct(5);

        Assert.That(product.Id, Is.Not.Null);
        Assert.That(product.SellerId, Is.EqualTo(_sellerId));
        Assert.That(product.Name, Is.EqualTo("Test Product"));
        Assert.That(product.Description, Is.EqualTo("A great product"));
        Assert.That(product.CategoryId, Is.EqualTo(_categoryId));
        Assert.That(product.Price, Is.EqualTo(_price));
        Assert.That(product.StockQuantity,Is.EqualTo(5));
        Assert.That(product.CreatedAt, Is.Not.EqualTo(default(DateTime)));
        Assert.That(product.UpdatedAt, Is.Null);
    }

    [Test]
    public void Create_ShouldRaiseProductCreatedEvent()
    {
        var product = CreateProduct();

        Assert.That(product.DomainEvents, Has.Count.EqualTo(1));
        var evt = product.DomainEvents[0] as ProductCreatedEvent;
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.ProductId.Value,  Is.EqualTo(product.Id.Value));
        Assert.That(evt.SellerId, Is.EqualTo(_sellerId));
        Assert.That(evt.Name, Is.EqualTo("Test Product"));
        Assert.That(evt.CategoryId, Is.EqualTo(_categoryId));
        Assert.That(evt.Price, Is.EqualTo(_price));
        Assert.That(evt.InitialStock, Is.EqualTo(10));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Create_WithEmptyName_ShouldThrowDomainException(string name)
    {
        var ex = Assert.Throws<DomainException>(() =>
            Product.Create(_sellerId, name, "desc", _categoryId, _price, 5, [], []));

        Assert.That(ex!.Message, Does.Contain("Product name cannot be empty"));
    }

    [Test]
    public void Create_WithNegativeStock_ShouldThrowDomainException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Product.Create(_sellerId, "Name", "desc", _categoryId, _price, -1, [], []));

        Assert.That(ex!.Message, Does.Contain("Initial stock cannot be negative"));
    }

    [Test]
    public void Create_WithZeroStock_ShouldSucceed()
    {
        var product = CreateProduct(0);

        Assert.That(product.StockQuantity, Is.EqualTo(0));
        Assert.That(product.Status, Is.EqualTo(ProductStatus.PendingReview));
    }

    [Test]
    public void Create_WithNullAttributes_ShouldDefaultToEmptyList()
    {
        var product = Product.Create(_sellerId, "Name", "desc", _categoryId, _price, 5, null!, []);

        Assert.That(product.Attributes, Is.Empty);
    }

    [Test]
    public void Create_WithNullImageUrls_ShouldDefaultToEmptyList()
    {
        var product = Product.Create(_sellerId, "Name", "desc", _categoryId, _price, 5, [], null!);

        Assert.That(product.ImageUrls, Is.Empty);
    }

    [Test]
    public void Create_ShouldStoreAttributesAndImageUrls()
    {
        var product = CreateProduct();

        Assert.That(product.Attributes, Has.Count.EqualTo(2));
        Assert.That(product.ImageUrls,  Has.Count.EqualTo(2));
    }

    #endregion

    #region Update

    [Test]
    public void Update_ShouldUpdateAllProperties()
    {
        var product = CreateProduct();
        var newCategoryId = CategoryId.CreateUnique();
        var newPrice = Money.Create(199.99m, "USD");
        product.ClearDomainEvents();

        product.Update("New Name", "New Desc", newCategoryId, newPrice, [], []);

        Assert.That(product.Name, Is.EqualTo("New Name"));
        Assert.That(product.Description, Is.EqualTo("New Desc"));
        Assert.That(product.CategoryId, Is.EqualTo(newCategoryId));
        Assert.That(product.Price, Is.EqualTo(newPrice));
        Assert.That(product.UpdatedAt, Is.Not.Null);
    }

    [Test]
    public void Update_ShouldRaiseProductUpdatedEvent()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Update("New Name", "New Desc", _categoryId, _price, [], []);

        Assert.That(product.DomainEvents, Has.Count.EqualTo(1));
        var evt = product.DomainEvents[0] as ProductUpdatedEvent;
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Name, Is.EqualTo("New Name"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Update_WithEmptyName_ShouldThrowDomainException(string name)
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.Update(name, "desc", _categoryId, _price, [], []));
    }

    [Test]
    public void Update_DeletedProduct_ShouldThrowInvalidProductOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<InvalidProductOperationException>(() =>
            product.Update("Name", "desc", _categoryId, _price, [], []));
    }

    #endregion

    #region UpdateStock

    [Test]
    public void UpdateStock_ShouldUpdateQuantity()
    {
        var product = CreateProduct(10);
        product.ClearDomainEvents();

        product.UpdateStock(25);

        Assert.That(product.StockQuantity, Is.EqualTo(25));
        Assert.That(product.UpdatedAt,     Is.Not.Null);
    }

    [Test]
    public void UpdateStock_ShouldRaiseProductStockUpdatedEvent()
    {
        var product = CreateProduct(10);
        product.ClearDomainEvents();

        product.UpdateStock(20);

        var stockEvent = product.DomainEvents.OfType<ProductStockUpdatedEvent>().Single();
        Assert.That(stockEvent.PreviousQuantity, Is.EqualTo(10));
        Assert.That(stockEvent.NewQuantity,      Is.EqualTo(20));
    }

    [Test]
    public void UpdateStock_WithNegativeQuantity_ShouldThrowDomainException()
    {
        var product = CreateProduct();

        var ex = Assert.Throws<DomainException>(() => product.UpdateStock(-1));

        Assert.That(ex!.Message, Does.Contain("Stock quantity cannot be negative"));
    }

    [Test]
    public void UpdateStock_DeletedProduct_ShouldThrowInvalidProductOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<InvalidProductOperationException>(() => product.UpdateStock(5));
    }

    [Test]
    public void UpdateStock_ActiveProduct_SetToZero_ShouldChangeStatusToOutOfStock()
    {
        var product = CreateActiveProduct(10);

        product.UpdateStock(0);

        Assert.That(product.Status, Is.EqualTo(ProductStatus.OutOfStock));
        var statusEvent = product.DomainEvents.OfType<ProductStatusChangedEvent>().Single();
        Assert.That(statusEvent.NewStatus, Is.EqualTo("OutOfStock"));
    }

    [Test]
    public void UpdateStock_ActiveProduct_SetToZero_ShouldRaiseTwoEvents()
    {
        var product = CreateActiveProduct(10);

        product.UpdateStock(0);

        Assert.That(product.DomainEvents, Has.Count.EqualTo(2));
    }

    [Test]
    public void UpdateStock_OutOfStockProduct_SetToPositive_ShouldChangeStatusToActive()
    {
        var product = CreateActiveProduct(10);
        product.UpdateStock(0); // becomes OutOfStock
        product.ClearDomainEvents();

        product.UpdateStock(5);

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Active));
        var statusEvent = product.DomainEvents.OfType<ProductStatusChangedEvent>().Single();
        Assert.That(statusEvent.NewStatus, Is.EqualTo("Active"));
    }

    [Test]
    public void UpdateStock_PendingReviewProduct_SetToZero_ShouldNotChangeStatus()
    {
        var product = CreateProduct(10);
        product.ClearDomainEvents();

        product.UpdateStock(0);

        Assert.That(product.Status, Is.EqualTo(ProductStatus.PendingReview));
        Assert.That(product.DomainEvents, Has.Count.EqualTo(1)); // only stock event, no status event
    }

    #endregion

    #region Activate

    [Test]
    public void Activate_FromPendingReview_ShouldChangeStatusToActive()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Activate();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Active));
    }

    [Test]
    public void Activate_FromPendingReview_ShouldRaiseProductStatusChangedEvent()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Activate();

        Assert.That(product.DomainEvents, Has.Count.EqualTo(1));
        var evt = product.DomainEvents[0] as ProductStatusChangedEvent;
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.PreviousStatus, Is.EqualTo("PendingReview"));
        Assert.That(evt.NewStatus,       Is.EqualTo("Active"));
    }

    [Test]
    public void Activate_FromInactive_ShouldChangeStatusToActive()
    {
        var product = CreateActiveProduct();
        product.Deactivate();
        product.ClearDomainEvents();

        product.Activate();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Active));
    }

    [Test]
    public void Activate_FromDeleted_ShouldThrowInvalidOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<DomainException>(() => product.Activate());
    }

    [Test]
    public void Activate_FromActive_ShouldThrowInvalidOperationException()
    {
        var product = CreateActiveProduct();

        Assert.Throws<DomainException>(() => product.Activate());
    }

    #endregion

    #region Deactivate

    [Test]
    public void Deactivate_FromActive_ShouldChangeStatusToInactive()
    {
        var product = CreateActiveProduct();

        product.Deactivate();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Inactive));
    }

    [Test]
    public void Deactivate_FromActive_ShouldRaiseProductStatusChangedEvent()
    {
        var product = CreateActiveProduct();

        product.Deactivate();

        var evt = product.DomainEvents.OfType<ProductStatusChangedEvent>().Single();
        Assert.That(evt.PreviousStatus, Is.EqualTo("Active"));
        Assert.That(evt.NewStatus,      Is.EqualTo("Inactive"));
    }

    [Test]
    public void Deactivate_FromPendingReview_ShouldThrowInvalidOperationException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.Deactivate());
    }

    [Test]
    public void Deactivate_FromDeleted_ShouldThrowInvalidOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<DomainException>(() => product.Deactivate());
    }

    #endregion

    #region Delete

    [Test]
    public void Delete_FromActive_ShouldChangeStatusToDeleted()
    {
        var product = CreateActiveProduct();
        product.ClearDomainEvents();

        product.Delete();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Deleted));
    }

    [Test]
    public void Delete_FromActive_ShouldRaiseTwoEvents()
    {
        var product = CreateActiveProduct();
        product.ClearDomainEvents();

        product.Delete();

        // ProductStatusChangedEvent + ProductDeletedEvent
        Assert.That(product.DomainEvents, Has.Count.EqualTo(2));
    }

    [Test]
    public void Delete_FromActive_ShouldRaiseProductStatusChangedEvent()
    {
        var product = CreateActiveProduct();
        product.ClearDomainEvents();

        product.Delete();

        var statusEvt = product.DomainEvents.OfType<ProductStatusChangedEvent>().Single();
        Assert.That(statusEvt.PreviousStatus, Is.EqualTo("Active"));
        Assert.That(statusEvt.NewStatus,      Is.EqualTo("Deleted"));
    }

    [Test]
    public void Delete_FromActive_ShouldRaiseProductDeletedEvent()
    {
        var product = CreateActiveProduct();
        product.ClearDomainEvents();

        product.Delete();

        var deletedEvt = product.DomainEvents.OfType<ProductDeletedEvent>().Single();
        Assert.That(deletedEvt.ProductId.Value, Is.EqualTo(product.Id.Value));
    }

    [Test]
    public void Delete_FromPendingReview_ShouldSucceed()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Delete();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Deleted));
    }

    [Test]
    public void Delete_FromDeleted_ShouldThrowInvalidOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<DomainException>(() => product.Delete());
    }

    #endregion

    #region AdjustStock

    [Test]
    public void AdjustStock_PositiveDelta_ShouldIncreaseStock()
    {
        var product = CreateActiveProduct(10);
        product.ClearDomainEvents();

        product.AdjustStock(5);

        Assert.That(product.StockQuantity, Is.EqualTo(15));
    }

    [Test]
    public void AdjustStock_NegativeDelta_ShouldDecreaseStock()
    {
        var product = CreateActiveProduct(10);
        product.ClearDomainEvents();

        product.AdjustStock(-3);

        Assert.That(product.StockQuantity, Is.EqualTo(7));
    }

    [Test]
    public void AdjustStock_NegativeDeltaExceedingStock_ShouldClampToZero()
    {
        var product = CreateActiveProduct(5);
        product.ClearDomainEvents();

        product.AdjustStock(-100);

        Assert.That(product.StockQuantity, Is.EqualTo(0));
    }

    [Test]
    public void AdjustStock_ShouldRaiseProductStockUpdatedEvent()
    {
        var product = CreateActiveProduct(10);
        product.ClearDomainEvents();

        product.AdjustStock(-4);

        var stockEvent = product.DomainEvents.OfType<ProductStockUpdatedEvent>().Single();
        Assert.That(stockEvent.PreviousQuantity, Is.EqualTo(10));
        Assert.That(stockEvent.NewQuantity, Is.EqualTo(6));
    }

    [Test]
    public void AdjustStock_ActiveProduct_WhenStockReachesZero_ShouldChangeStatusToOutOfStock()
    {
        var product = CreateActiveProduct(5);

        product.AdjustStock(-5);

        Assert.That(product.Status, Is.EqualTo(ProductStatus.OutOfStock));
    }

    [Test]
    public void AdjustStock_ActiveProduct_WhenStockReachesZero_ShouldRaiseTwoEvents()
    {
        var product = CreateActiveProduct(5);

        product.AdjustStock(-5);

        // ProductStockUpdatedEvent + ProductStatusChangedEvent
        Assert.That(product.DomainEvents, Has.Count.EqualTo(2));
    }

    [Test]
    public void AdjustStock_OutOfStockProduct_WhenPositiveDelta_ShouldChangeStatusToActive()
    {
        var product = CreateActiveProduct(5);
        product.AdjustStock(-5); // becomes OutOfStock
        product.ClearDomainEvents();

        product.AdjustStock(3);

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Active));
    }

    [Test]
    public void AdjustStock_DeletedProduct_ShouldThrowInvalidProductOperationException()
    {
        var product = CreateActiveProduct();
        product.Delete();

        Assert.Throws<InvalidProductOperationException>(() => product.AdjustStock(-1));
    }

    [Test]
    public void AdjustStock_ShouldSetUpdatedAt()
    {
        var product = CreateActiveProduct(10);

        product.AdjustStock(1);

        Assert.That(product.UpdatedAt, Is.Not.Null);
    }

    #endregion

    #region DomainEvents

    [Test]
    public void ClearDomainEvents_ShouldRemoveAllEvents()
    {
        var product = CreateProduct(); // raises ProductCreatedEvent

        product.ClearDomainEvents();

        Assert.That(product.DomainEvents, Is.Empty);
    }

    [Test]
    public void Create_ThenActivate_ThenDelete_ShouldAccumulateEvents()
    {
        var product = CreateProduct(); // +1 = ProductCreatedEvent

        product.Activate();                // +1 = ProductStatusChangedEvent
        product.Delete();                  // +2 = ProductStatusChangedEvent + ProductDeletedEvent

        Assert.That(product.DomainEvents, Has.Count.EqualTo(4));
    }

    #endregion

    #region Approve

    [Test]
    public void Approve_FromPendingReview_ShouldChangeStatusToActive()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Approve();

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Active));
    }

    [Test]
    public void Approve_ShouldRaiseProductApprovedEvent()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Approve();

        var evt = product.DomainEvents.OfType<ProductApprovedEvent>().Single();
        Assert.That(evt.ProductId.Value, Is.EqualTo(product.Id.Value));
    }

    [Test]
    public void Approve_ShouldClearReviewNotes()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Approve();

        Assert.That(product.ReviewNotes, Is.Null);
    }

    [Test]
    public void Approve_FromActive_ShouldThrowDomainException()
    {
        var product = CreateActiveProduct();

        Assert.Throws<DomainException>(() => product.Approve());
    }

    [Test]
    public void Approve_ShouldSetUpdatedAt()
    {
        var product = CreateProduct();

        product.Approve();

        Assert.That(product.UpdatedAt, Is.Not.Null);
    }

    #endregion

    #region Reject

    [Test]
    public void Reject_FromPendingReview_ShouldChangeStatusToRejected()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Reject("Bad images");

        Assert.That(product.Status, Is.EqualTo(ProductStatus.Rejected));
    }

    [Test]
    public void Reject_ShouldSetReviewNotes()
    {
        var product = CreateProduct();

        product.Reject("Inappropriate content");

        Assert.That(product.ReviewNotes, Is.EqualTo("Inappropriate content"));
    }

    [Test]
    public void Reject_ShouldTrimReasonWhitespace()
    {
        var product = CreateProduct();

        product.Reject("  bad images  ");

        Assert.That(product.ReviewNotes, Is.EqualTo("bad images"));
    }

    [Test]
    public void Reject_ShouldRaiseProductRejectedEvent()
    {
        var product = CreateProduct();
        product.ClearDomainEvents();

        product.Reject("reason");

        var evt = product.DomainEvents.OfType<ProductRejectedEvent>().Single();
        Assert.That(evt.ProductId.Value, Is.EqualTo(product.Id.Value));
        Assert.That(evt.Reason, Is.EqualTo("reason"));
    }

    [Test]
    public void Reject_WithEmptyReason_ShouldThrowDomainException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.Reject(""));
    }

    [Test]
    public void Reject_WithWhitespaceReason_ShouldThrowDomainException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.Reject("   "));
    }

    [Test]
    public void Reject_FromActive_ShouldThrowDomainException()
    {
        var product = CreateActiveProduct();

        Assert.Throws<DomainException>(() => product.Reject("reason"));
    }

    [Test]
    public void Reject_ShouldSetUpdatedAt()
    {
        var product = CreateProduct();

        product.Reject("reason");

        Assert.That(product.UpdatedAt, Is.Not.Null);
    }

    #endregion
}
