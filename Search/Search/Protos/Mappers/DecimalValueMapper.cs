namespace Protos.Common;

public static class DecimalValueMapper
{
    public static decimal ToDecimal(DecimalValue? value)
    {
        if (value is null) return 0m;
        return value.Units + value.Nanos / 1_000_000_000m;
    }

    public static DecimalValue ToProto(decimal value)
    {
        var units = (long)value;
        var nanos = (int)((value - units) * 1_000_000_000m);
        return new DecimalValue { Units = units, Nanos = nanos };
    }
}
