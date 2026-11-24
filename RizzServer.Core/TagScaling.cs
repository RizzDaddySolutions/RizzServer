using System.Globalization;

namespace RizzServer.Core;

/// <summary>Linear scaling and range checks of a tag: the device holds raw values, clients see engineering units.</summary>
public static class TagScaling
{
    private static bool TryNumber(object? v, out double d)
    {
        switch (v)
        {
            case null or string or bool: d = 0; return false;
            case IConvertible c:
                try { d = c.ToDouble(CultureInfo.InvariantCulture); return double.IsFinite(d); }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { d = 0; return false; }
            default: d = 0; return false;
        }
    }

    /// <summary>raw × Scale + Offset. Values that aren't numbers (strings, booleans) pass through.</summary>
    public static object? ToEngineering(TagConfig tag, object? raw)
    {
        if (!tag.HasScaling || !TryNumber(raw, out var d)) return raw;
        return d * (tag.Scale ?? 1) + (tag.Offset ?? 0);
    }

    /// <summary>The inverse, for writes: (engineering − Offset) / Scale. A zero scale can't be inverted and is left as is.</summary>
    public static object ToRaw(TagConfig tag, object engineering)
    {
        if (!tag.HasScaling || !TryNumber(engineering, out var d)) return engineering;
        var scale = tag.Scale ?? 1;
        if (scale == 0) return engineering;
        return (d - (tag.Offset ?? 0)) / scale;
    }

    public static bool WithinRange(TagConfig tag, object? value)
    {
        if ((tag.EuMin is null && tag.EuMax is null) || !TryNumber(value, out var d)) return true;
        return (tag.EuMin is null || d >= tag.EuMin) && (tag.EuMax is null || d <= tag.EuMax);
    }
}
