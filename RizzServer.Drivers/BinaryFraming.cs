using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RizzServer.Drivers;

public enum ChecksumKind { None, Sum8, Xor8, Crc16Modbus }

/// <summary>
/// Cuts a raw byte stream into frames: sync on a header, take a fixed length or a length field,
/// verify an optional trailing checksum, and resynchronise byte by byte on any mismatch.
/// </summary>
public sealed class FrameAssembler
{
    private readonly List<byte> _buf = new();

    public byte[] Header { get; init; } = [];
    /// <summary>Total frame length in bytes; 0 means read it from the length field.</summary>
    public int FixedLength { get; init; }
    public int LengthOffset { get; init; }
    public int LengthSize { get; init; } = 1;
    public bool LengthBigEndian { get; init; } = true;
    /// <summary>Added to the length-field value to get the total frame length.</summary>
    public int LengthAdjust { get; init; }
    public ChecksumKind Checksum { get; init; }
    /// <summary>Index of the first byte covered by the checksum (checksum itself sits at the frame end).</summary>
    public int ChecksumStart { get; init; }
    public int MaxFrame { get; init; } = 1024;

    private int ChecksumLength => Checksum switch
    {
        ChecksumKind.None => 0,
        ChecksumKind.Crc16Modbus => 2,
        _ => 1,
    };

    public int DroppedBytes { get; private set; }

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data.ToArray());
        var frames = new List<byte[]>();

        while (true)
        {
            // Sync on header.
            if (Header.Length > 0)
            {
                var idx = IndexOfHeader();
                if (idx < 0)
                {
                    // Keep a possible partial header at the tail.
                    var keep = Math.Min(Header.Length - 1, _buf.Count);
                    Drop(_buf.Count - keep);
                    break;
                }
                if (idx > 0) Drop(idx);
            }

            var len = FrameLengthOrNeedMore(out var needMore);
            if (needMore) break;
            if (len < 0) { Drop(1); continue; } // implausible length: resync

            if (_buf.Count < len) break;

            var frame = _buf.GetRange(0, len).ToArray();
            if (!ChecksumOk(frame)) { Drop(1); continue; }

            _buf.RemoveRange(0, len);
            frames.Add(frame);
        }

        if (_buf.Count > MaxFrame * 4) { DroppedBytes += _buf.Count; _buf.Clear(); }
        return frames;
    }

    public void Reset() => _buf.Clear();

    private void Drop(int n)
    {
        if (n <= 0) return;
        n = Math.Min(n, _buf.Count);
        _buf.RemoveRange(0, n);
        DroppedBytes += n;
    }

    private int IndexOfHeader()
    {
        for (var i = 0; i + Header.Length <= _buf.Count; i++)
        {
            var match = true;
            for (var j = 0; j < Header.Length; j++)
                if (_buf[i + j] != Header[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    /// <returns>Frame length, -1 if implausible. <paramref name="needMore"/> is set when the length isn't known yet.</returns>
    private int FrameLengthOrNeedMore(out bool needMore)
    {
        needMore = false;
        int len;
        if (FixedLength > 0) len = FixedLength;
        else
        {
            if (_buf.Count < LengthOffset + LengthSize) { needMore = true; return 0; }
            var raw = 0;
            for (var i = 0; i < LengthSize; i++)
            {
                var b = _buf[LengthOffset + (LengthBigEndian ? i : LengthSize - 1 - i)];
                raw = (raw << 8) | b;
            }
            len = raw + LengthAdjust;
        }

        var min = Math.Max(Header.Length, LengthOffset + LengthSize) + ChecksumLength;
        if (Checksum != ChecksumKind.None) min = Math.Max(min, ChecksumStart + ChecksumLength);
        return len < Math.Max(min, 1) || len > MaxFrame ? -1 : len;
    }

    private bool ChecksumOk(byte[] f)
    {
        if (Checksum == ChecksumKind.None) return true;
        var end = f.Length - ChecksumLength;
        if (ChecksumStart > end) return false;
        var span = f.AsSpan(ChecksumStart, end - ChecksumStart);
        switch (Checksum)
        {
            case ChecksumKind.Sum8:
                byte sum = 0; foreach (var b in span) sum += b;
                return sum == f[^1];
            case ChecksumKind.Xor8:
                byte x = 0; foreach (var b in span) x ^= b;
                return x == f[^1];
            default:
                return Crc16Modbus(span) == (ushort)(f[^2] | (f[^1] << 8));
        }
    }

    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte[] ParseHex(string text)
    {
        var clean = Regex.Replace(text, @"0x|[\s,\-:]", "", RegexOptions.IgnoreCase);
        return clean.Length == 0 ? [] : Convert.FromHexString(clean);
    }
}

/// <summary>
/// Where and how to read one value from a frame. Address: <c>offset:type[:scale]</c>.
/// Types: u8 i8 u16 i16 u32 i32 u64 i64 f32 f64 (optional le/be suffix, e.g. i16le),
/// bit0..bit7 (bit within the byte at offset), ascii4 (4 bytes as text).
/// </summary>
public sealed record BinaryFieldSpec(int Offset, string Type, bool? BigEndian, double Scale, int Size)
{
    private static readonly Regex TypeRegex = new(
        @"^(?<t>u8|i8|u16|i16|u32|i32|u64|i64|f32|f64|bit[0-7]|ascii\d+)(?<e>le|be)?$", RegexOptions.IgnoreCase);

    public static BinaryFieldSpec Parse(string address)
    {
        var parts = address.Split(':');
        if (parts.Length < 2 || parts.Length > 3 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new FormatException($"Bad binary address '{address}' (use offset:type[:scale], e.g. 4:i16:0.1).");

        var m = TypeRegex.Match(parts[1].Trim());
        if (!m.Success) throw new FormatException($"Unknown type '{parts[1]}' in '{address}'.");

        var type = m.Groups["t"].Value.ToLowerInvariant();
        bool? big = m.Groups["e"].Success ? m.Groups["e"].Value.Equals("be", StringComparison.OrdinalIgnoreCase) : null;
        var scale = 1.0;
        if (parts.Length == 3 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            throw new FormatException($"Bad scale '{parts[2]}' in '{address}'.");

        var size = type switch
        {
            "u8" or "i8" => 1,
            "u16" or "i16" => 2,
            "u32" or "i32" or "f32" => 4,
            "u64" or "i64" or "f64" => 8,
            _ when type.StartsWith("bit") => 1,
            _ => int.Parse(type[5..], CultureInfo.InvariantCulture),
        };
        return new BinaryFieldSpec(offset, type, big, scale, size);
    }

    /// <summary>Returns the decoded value, or null if the frame is too short.</summary>
    public object? Decode(ReadOnlySpan<byte> frame, bool defaultBigEndian)
    {
        if (Offset + Size > frame.Length) return null;
        var s = frame.Slice(Offset, Size);
        var big = BigEndian ?? defaultBigEndian;

        object value = Type switch
        {
            "u8" => s[0],
            "i8" => (sbyte)s[0],
            "u16" => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
            "i16" => big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
            "u32" => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
            "i32" => big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            "u64" => big ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
            "i64" => big ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
            "f32" => big ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
            "f64" => big ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
            _ when Type.StartsWith("bit") => (s[0] >> (Type[3] - '0') & 1) != 0,
            _ => Encoding.Latin1.GetString(s).TrimEnd('\0', ' '),
        };

        if (Scale == 1.0 || value is bool or string) return value;
        return Convert.ToDouble(value, CultureInfo.InvariantCulture) * Scale;
    }
}
