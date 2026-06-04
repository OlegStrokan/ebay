using Domain.Exceptions;

namespace Domain.ValueObjects;

public sealed class ProductStatus
{
    public static readonly ProductStatus Draft = new("Draft", 0);
    public static readonly ProductStatus Approved = new("Approved", 1);
    public static readonly ProductStatus Inactive = new("Inactive", 2);
    public static readonly ProductStatus OutOfStock = new("OutOfStock", 3);
    public static readonly ProductStatus Deleted = new("Deleted", 4);
    public static readonly ProductStatus PendingApproval = new("PendingApproval", 5);
    public static readonly ProductStatus Rejected = new("Rejected", 6);

    public string Name { get; }
    public int Value { get; }

    private readonly HashSet<ProductStatus> _allowedTransitions;
    public IReadOnlyCollection<ProductStatus> AllowedTransitions => _allowedTransitions;

    private ProductStatus(string name, int value)
    {
        Name = name;
        Value = value;
        _allowedTransitions = new HashSet<ProductStatus>();
    }

    static ProductStatus()
    {
        Draft.AllowsTransitionTo(PendingApproval, Deleted);
        PendingApproval.AllowsTransitionTo(Approved, Rejected, Deleted);
        Rejected.AllowsTransitionTo(PendingApproval, Deleted);
        Approved.AllowsTransitionTo(Inactive, OutOfStock, Deleted);
        Inactive.AllowsTransitionTo(Approved, Deleted);
        OutOfStock.AllowsTransitionTo(Approved, Deleted);
        Deleted.AllowsTransitionTo();
    }

    private void AllowsTransitionTo(params ProductStatus[] targets)
    {
        foreach (var target in targets)
            _allowedTransitions.Add(target);
    }

    public bool CanTransitionTo(ProductStatus target) => _allowedTransitions.Contains(target);

    public void ValidateTransitionTo(ProductStatus target)
    {
        if (!CanTransitionTo(target))
            throw new DomainException(
                $"Cannot transition from {Name} to {target.Name}. "
                + $"Allowed transitions: {string.Join(", ", _allowedTransitions.Select(t => t.Name))}");
    }

    public static ProductStatus FromValue(int value) => value switch
    {
        0 => Draft,
        1 => Approved,
        2 => Inactive,
        3 => OutOfStock,
        4 => Deleted,
        5 => PendingApproval,
        6 => Rejected,
        _ => throw new InvalidValueException($"Unknown ProductStatus value: {value}")
    };

    public static ProductStatus FromName(string name) => name switch
    {
        "Draft" => Draft,
        "Approved" => Approved,
        "Inactive" => Inactive,
        "OutOfStock" => OutOfStock,
        "Deleted" => Deleted,
        "PendingApproval" => PendingApproval,
        "Rejected" => Rejected,
        _ => throw new InvalidValueException($"Unknown ProductStatus name: {name}")
    };

    public override bool Equals(object? obj) => obj is ProductStatus other && Value == other.Value;
    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Name;
}