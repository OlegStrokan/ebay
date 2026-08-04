using Protos.Common;

namespace Api.Mappers;

internal static class DecimalValueMapper
{
    private const decimal NanoFactor = 1_000_000_000m;

    public static decimal ToDecimal(this DecimalValue grpcValue)
    {
        return grpcValue.Units + (grpcValue.Nanos / NanoFactor);
    }

    public static DecimalValue ToDecimalValue(this decimal value)
    {
        var units = decimal.Truncate(value);
        var nanos = (int)((value - units) * NanoFactor);
        return new DecimalValue { Units = (long)units, Nanos = nanos };
    }
}
