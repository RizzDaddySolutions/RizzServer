using System.Globalization;

namespace RizzServer.Core;

public static class TagValueConverter
{
    public static Type ClrType(TagDataType t) => t switch
    {
        TagDataType.Boolean => typeof(bool),
        TagDataType.Int16 => typeof(short),
        TagDataType.UInt16 => typeof(ushort),
        TagDataType.Int32 => typeof(int),
        TagDataType.UInt32 => typeof(uint),
        TagDataType.Int64 => typeof(long),
        TagDataType.UInt64 => typeof(ulong),
        TagDataType.Float => typeof(float),
        TagDataType.Double => typeof(double),
        _ => typeof(string),
    };

    /// <summary>Number of 16-bit registers a type occupies.</summary>
    public static ushort RegisterCount(TagDataType t) => t switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Double => 4,
        _ => 1,
    };

    /// <summary>Converts any raw value (number, string, bool) to the tag's CLR type. Returns null if impossible.</summary>
    public static object? Coerce(object? raw, TagDataType type)
    {
        if (raw is null) return null;
        try
        {
            if (type == TagDataType.String)
                return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";

            if (type == TagDataType.Boolean)
            {
                if (raw is string s)
                {
                    s = s.Trim();
                    if (bool.TryParse(s, out var b)) return b;
                    return double.Parse(s, CultureInfo.InvariantCulture) != 0;
                }
                return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            }

            if (raw is string str) raw = str.Trim();
            return Convert.ChangeType(raw, ClrType(type), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
