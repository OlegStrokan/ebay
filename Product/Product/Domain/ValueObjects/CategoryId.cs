using Domain.Exceptions;

namespace Domain.ValueObjects;

public sealed record CategoryId
{
    public static readonly Guid PlaceholderGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly CategoryId Placeholder = new(PlaceholderGuid);

    public Guid Value { get; init; }

    private CategoryId(Guid value)
    {
        if (value == Guid.Empty)
            throw new InvalidValueException("CategoryId cannot be empty");
        Value = value;
    }

    public static CategoryId From(Guid value) => new(value);
    public static CategoryId CreateUnique() => new(Guid.NewGuid());
    public static bool IsPlaceholder(CategoryId categoryId) => categoryId.Value == PlaceholderGuid;
    public bool IsPlaceholder() => Value == PlaceholderGuid;

    public override string ToString() => Value.ToString();
}
