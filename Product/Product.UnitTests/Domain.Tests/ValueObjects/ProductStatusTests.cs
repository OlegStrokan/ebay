using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Tests.ValueObjects;

[TestFixture]
public class ProductStatusTests
{
    [Test]
    public void Draft_CanTransitionTo_PendingApproval() =>
        Assert.That(ProductStatus.Draft.CanTransitionTo(ProductStatus.PendingApproval), Is.True);

    [Test]
    public void PendingApproval_CanTransitionTo_Approved() =>
        Assert.That(ProductStatus.PendingApproval.CanTransitionTo(ProductStatus.Approved), Is.True);

    [Test]
    public void PendingApproval_CanTransitionTo_Rejected() =>
        Assert.That(ProductStatus.PendingApproval.CanTransitionTo(ProductStatus.Rejected), Is.True);

    [Test]
    public void Approved_CanTransitionTo_Inactive_And_OutOfStock() 
    {
        Assert.That(ProductStatus.Approved.CanTransitionTo(ProductStatus.Inactive), Is.True);
        Assert.That(ProductStatus.Approved.CanTransitionTo(ProductStatus.OutOfStock), Is.True);
    }

    [Test]
    public void Rejected_CanTransitionTo_PendingApproval() =>
        Assert.That(ProductStatus.Rejected.CanTransitionTo(ProductStatus.PendingApproval), Is.True);

    [Test]
    public void Draft_CannotTransitionTo_Approved() =>
        Assert.That(ProductStatus.Draft.CanTransitionTo(ProductStatus.Approved), Is.False);

    [Test]
    public void Deleted_CannotTransitionTo_AnyStatus()
    {
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.Draft), Is.False);
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.PendingApproval), Is.False);
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.Approved), Is.False);
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.Inactive), Is.False);
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.OutOfStock), Is.False);
        Assert.That(ProductStatus.Deleted.CanTransitionTo(ProductStatus.Rejected), Is.False);
    }

    [Test]
    public void ValidateTransitionTo_WithInvalidTransition_ShouldThrowDomainException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            ProductStatus.Draft.ValidateTransitionTo(ProductStatus.Inactive));

        Assert.That(ex!.Message, Does.Contain("Cannot transition from Draft to Inactive"));
    }

    [Test]
    public void ValidateTransitionTo_WithValidTransition_ShouldNotThrow()
    {
        Assert.DoesNotThrow(() => ProductStatus.Draft.ValidateTransitionTo(ProductStatus.PendingApproval));
    }

    [TestCase(0, "Draft")]
    [TestCase(1, "Approved")]
    [TestCase(2, "Inactive")]
    [TestCase(3, "OutOfStock")]
    [TestCase(4, "Deleted")]
    [TestCase(5, "PendingApproval")]
    [TestCase(6, "Rejected")]
    public void FromValue_ShouldReturnCanonicalStatus(int value, string expectedName)
    {
        var status = ProductStatus.FromValue(value);

        Assert.That(status.Name, Is.EqualTo(expectedName));
    }

    [TestCase("Draft", "Draft")]
    [TestCase("PendingApproval", "PendingApproval")]
    [TestCase("Approved", "Approved")]
    [TestCase("Inactive", "Inactive")]
    [TestCase("OutOfStock", "OutOfStock")]
    [TestCase("Deleted", "Deleted")]
    [TestCase("Rejected", "Rejected")]
    public void FromName_ShouldReturnExpectedCanonicalStatus(string name, string expectedCanonicalName)
    {
        var status = ProductStatus.FromName(name);

        Assert.That(status.Name, Is.EqualTo(expectedCanonicalName));
    }

    [Test]
    public void FromValue_WithUnknownValue_ShouldThrowInvalidValueException()
    {
        Assert.Throws<InvalidValueException>(() => ProductStatus.FromValue(99));
    }

    [Test]
    public void FromName_WithUnknownName_ShouldThrowInvalidValueException()
    {
        Assert.Throws<InvalidValueException>(() => ProductStatus.FromName("Unknown"));
    }

    [Test]
    public void ToString_ShouldReturnStatusName()
    {
        Assert.That(ProductStatus.Approved.ToString(), Is.EqualTo("Approved"));
        Assert.That(ProductStatus.PendingApproval.ToString(), Is.EqualTo("PendingApproval"));
        Assert.That(ProductStatus.Deleted.ToString(), Is.EqualTo("Deleted"));
    }

    [Test]
    public void Equality_ShouldUseValue()
    {
        var fromValue = ProductStatus.FromValue(1);
        Assert.That(fromValue.Equals(ProductStatus.Approved), Is.True);
        Assert.That(ProductStatus.Approved.Equals(ProductStatus.Inactive), Is.False);
        Assert.That(fromValue.GetHashCode(), Is.EqualTo(ProductStatus.Approved.GetHashCode()));
    }
}
