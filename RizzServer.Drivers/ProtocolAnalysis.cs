using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RizzServer.Drivers;

/// <summary>How a byte stream is cut into frames, as worked out from a capture.</summary>
public sealed record FramingGuess(
    byte[] Header,
    int FrameLength,            // > 0: fixed length; 0: read it from the length field
    int LengthOffset,
    int LengthSize,
    bool LengthBigEndian,
    int LengthAdjust,
    ChecksumKind Checksum,
    int ChecksumStart,
    int FrameCount,
    double Confidence)
{
    public bool IsFixedLength => FrameLength > 0;
    public int ChecksumLength => Checksum switch { ChecksumKind.None => 0, ChecksumKind.Crc16Modbus => 2, _ => 1 };

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("Header ").Append(Convert.ToHexString(Header));
        sb.Append(IsFixedLength ? $", {FrameLength} bytes per frame" : $", length in byte {LengthOffset} ({LengthSize} byte{(LengthSize == 2 ? "s" : "")}, adjust {LengthAdjust:+0;-0;0})");
        sb.Append(Checksum == ChecksumKind.None ? ", no checksum" : $", {Checksum} from byte {ChecksumStart}");
        sb.Append($" — {FrameCount} frames, {Confidence:P0} confidence");
        return sb.ToString();
    }

    /// <summary>The assembler that cuts frames the way this guess describes.</summary>
    public FrameAssembler CreateAssembler() => new()
    {
        Header = Header,
        FixedLength = FrameLength,
        LengthOffset = LengthOffset,
        LengthSize = LengthSize,
        LengthBigEndian = LengthBigEndian,
        LengthAdjust = LengthAdjust,
        Checksum = Checksum,
        ChecksumStart = ChecksumStart,
    };
}

/// <summary>Finds the framing of a binary protocol from raw captured bytes: header, length and checksum.</summary>
public static class FrameDetector
{
    private const int MaxAnalysed = 256 * 1024;
    private const int MinFrames = 4;

    /// <returns>A guess, or null when no repeating structure was found.</returns>
    public static FramingGuess? Detect(ReadOnlySpan<byte> capture)
    {
        var data = capture.Length > MaxAnalysed ? capture[..MaxAnalysed].ToArray() : capture.ToArray();
        if (data.Length < 32) return null;

        var candidates = Candidates(data);
        FramingGuess? best = null;
        double bestScore = 0;

        // 1) fixed length: following the header every L bytes keeps hitting the header again
        var fixedFound = new List<(FramingGuess Guess, double Score, int First)>();
        foreach (var c in candidates)
        {
            var gaps = new Dictionary<int, int>();
            for (var i = 1; i < c.Positions.Count; i++)
            {
                var gap = c.Positions[i] - c.Positions[i - 1];
                if (gap > c.Header.Length && gap <= 2048) gaps[gap] = gaps.GetValueOrDefault(gap) + 1;
            }

            // The true length is among the most common gaps even when the header byte also occurs inside payloads.
            foreach (var length in gaps.OrderByDescending(g => g.Value).Take(6).Select(g => g.Key))
            {
                var (frames, rate) = ChainFixed(data, c.Header, length);
                if (frames < MinFrames || rate < 0.85) continue;

                // Coverage dominates: a header byte that also shows up inside payloads must not win on raw count.
                var score = Math.Pow(rate, 4) * frames * (1 - 0.01 * c.Header.Length);
                fixedFound.Add((new FramingGuess(c.Header, length, 0, 1, true, 0, ChecksumKind.None, 0, frames, rate), score, c.Positions[0]));
            }
        }

        if (fixedFound.Count > 0)
        {
            // Any constant byte that repeats every L bytes looks like a header; the real frame start is the one
            // for which a checksum falls into place, then the longer header, then the earliest in the capture.
            var top = fixedFound.Max(f => f.Score);
            var chosen = fixedFound
                .Where(f => f.Score >= top * 0.98)
                .Take(40)
                .Select(f => (f.Guess, f.Score, f.First, Checked: WithChecksum(data, f.Guess)))
                .OrderBy(f => f.Checked.Checksum == ChecksumKind.None ? 1 : 0)
                .ThenByDescending(f => f.Guess.Header.Length)
                .ThenBy(f => f.First)
                .ThenByDescending(f => f.Score)
                .First();
            return chosen.Checked;
        }

        // 2) variable length: some byte after the header says how long the frame is
        if (best is null)
        {
            foreach (var c in candidates.Take(12))
            foreach (var size in new[] { 1, 2 })
            foreach (var big in size == 1 ? new[] { true } : new[] { true, false })
            for (var k = c.Header.Length; k <= Math.Min(c.Header.Length + 6, 12); k++)
            for (var adj = -6; adj <= 24; adj++)
            {
                var (frames, rate) = Chain(data, c.Header, k, size, big, adj);
                if (frames < MinFrames || rate < 0.9) continue;
                var score = Math.Pow(rate, 4) * frames;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new FramingGuess(c.Header, 0, k, size, big, adj, ChecksumKind.None, 0, frames, rate);
                }
            }
        }

        return best is null ? null : WithChecksum(data, best);
    }

    private sealed record Candidate(byte[] Header, List<int> Positions);

    /// <summary>The most frequent 1–3 byte sequences: possible headers.</summary>
    private static List<Candidate> Candidates(byte[] data)
    {
        var map = new Dictionary<int, List<int>>();
        for (var i = 0; i < data.Length; i++)
        {
            var key = 0;
            for (var n = 1; n <= 3 && i + n <= data.Length; n++)
            {
                key = (key << 8) | data[i + n - 1];
                var id = (n << 24) | key;
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<int>();
                list.Add(i);
            }
        }

        return map.Where(kv => kv.Value.Count >= MinFrames)
            .OrderByDescending(kv => kv.Value.Count)
            .Take(60)
            .Select(kv =>
            {
                var n = kv.Key >> 24;
                var header = new byte[n];
                for (var i = 0; i < n; i++) header[i] = (byte)(kv.Key >> (8 * (n - 1 - i)));
                return new Candidate(header, kv.Value);
            })
            .ToList();
    }

    /// <summary>Steps through the stream L bytes at a time from a header; returns how many frames chained and the hit rate.</summary>
    private static (int Frames, double Rate) ChainFixed(byte[] d, byte[] header, int length)
    {
        int p = IndexOf(d, header, 0), frames = 0, misses = 0;
        while (p >= 0 && p + length + header.Length <= d.Length)
        {
            if (Matches(d, p + length, header)) { frames++; p += length; }
            else { misses++; p = IndexOf(d, header, p + 1); }
        }
        var total = frames + misses;
        return (frames + 1, total == 0 ? 0 : (double)frames / total);
    }

    /// <summary>Walks the stream frame by frame using a length field; returns how many frames chained and the success rate.</summary>
    private static (int Frames, double Rate) Chain(byte[] d, byte[] header, int k, int size, bool big, int adj)
    {
        int p = IndexOf(d, header, 0), frames = 0, fails = 0;
        while (p >= 0 && p + k + size <= d.Length)
        {
            var raw = size == 1 ? d[p + k] : big ? (d[p + k] << 8) | d[p + k + 1] : (d[p + k + 1] << 8) | d[p + k];
            var len = raw + adj;
            if (len < k + size || len > 1024) { fails++; p = IndexOf(d, header, p + 1); continue; }

            var next = p + len;
            if (next + header.Length > d.Length) { frames++; break; }
            if (Matches(d, next, header)) { frames++; p = next; }
            else { fails++; p = IndexOf(d, header, p + 1); }
        }
        var total = frames + fails;
        return (frames, total == 0 ? 0 : (double)frames / total);
    }

    private static FramingGuess WithChecksum(byte[] data, FramingGuess guess)
    {
        var frames = guess.CreateAssembler().Push(data);
        if (frames.Count < MinFrames) return guess with { FrameCount = frames.Count };

        FramingGuess? best = null;
        var bestRate = 0.9;   // a checksum must hold for nearly every frame
        foreach (var kind in new[] { ChecksumKind.Sum8, ChecksumKind.Xor8, ChecksumKind.Crc16Modbus })
        foreach (var start in new[] { 0, guess.Header.Length })
        {
            var cs = kind == ChecksumKind.Crc16Modbus ? 2 : 1;
            var ok = frames.Count(f => f.Length > start + cs &&
                ComputeTail(kind, f, start).AsSpan().SequenceEqual(f.AsSpan(f.Length - cs)));
            var rate = (double)ok / frames.Count;
            if (rate > bestRate)
            {
                bestRate = rate;
                best = guess with { Checksum = kind, ChecksumStart = start };
            }
        }

        if (best is null) return guess with { FrameCount = frames.Count };

        // With the checksum known, count only frames that pass it.
        var valid = best.CreateAssembler().Push(data).Count;
        return best with { FrameCount = valid, Confidence = Math.Min(best.Confidence, bestRate) };
    }

    private static byte[] ComputeTail(ChecksumKind kind, byte[] frame, int start)
    {
        var cs = kind == ChecksumKind.Crc16Modbus ? 2 : 1;
        return FrameAssembler.ComputeChecksum(kind, frame.AsSpan(start, frame.Length - cs - start));
    }

    private static bool Matches(byte[] d, int at, byte[] pattern)
    {
        if (at < 0 || at + pattern.Length > d.Length) return false;
        for (var i = 0; i < pattern.Length; i++) if (d[at + i] != pattern[i]) return false;
        return true;
    }

    private static int IndexOf(byte[] d, byte[] pattern, int from)
    {
        for (var i = Math.Max(0, from); i + pattern.Length <= d.Length; i++) if (Matches(d, i, pattern)) return i;
        return -1;
    }
}

/// <summary>A field the analysis thinks is worth turning into a tag.</summary>
public sealed record FieldSuggestion(int Offset, string Type, string Name, string Reason, bool IsCounter = false);

/// <summary>Looks at many frames of one protocol and works out which bytes carry data.</summary>
public static class FrameAnalysis
{
    /// <summary>Cuts a capture into frames as the guess describes; <paramref name="dropped"/> counts bytes that fit no frame.</summary>
    public static List<byte[]> Cut(ReadOnlySpan<byte> capture, FramingGuess guess, out int dropped)
    {
        var assembler = guess.CreateAssembler();
        var frames = assembler.Push(capture).ToList();
        dropped = assembler.DroppedBytes;
        return frames;
    }

    /// <summary>Splits timestamped chunks into frames wherever the line was silent for at least <paramref name="gap"/>.</summary>
    public static List<byte[]> SplitByIdleGap(IEnumerable<(DateTime Time, byte[] Data)> chunks, TimeSpan gap)
    {
        var frames = new List<byte[]>();
        var current = new List<byte>();
        DateTime? last = null;
        foreach (var (time, data) in chunks)
        {
            if (last is not null && time - last >= gap && current.Count > 0)
            {
                frames.Add(current.ToArray());
                current.Clear();
            }
            current.AddRange(data);
            last = time;
        }
        if (current.Count > 0) frames.Add(current.ToArray());
        return frames;
    }

    /// <summary>
    /// Suggests fields among the varying bytes of equal-length frames. <paramref name="skip"/> marks header/length/checksum bytes.
    /// Every candidate window (1, 2 or 4 bytes, several readings) is scored by how smoothly its value moves from frame to frame,
    /// and the set of non-overlapping windows that covers all changing bytes with the best total score wins.
    /// </summary>
    public static List<FieldSuggestion> SuggestFields(IReadOnlyList<byte[]> frames, Func<int, bool> skip)
    {
        var result = new List<FieldSuggestion>();
        if (frames.Count < 3) return result;
        var length = frames.Min(f => f.Length);

        var varies = new bool[length];
        for (var o = 0; o < length; o++) varies[o] = frames.Any(f => f[o] != frames[0][o]);

        // A counter dominates any window it is part of (its steady climb makes the whole value look smooth),
        // so counters are always fields of their own.
        var counters = new bool[length];
        for (var o = 0; o < length; o++) counters[o] = varies[o] && !skip(o) && IsCounter(frames, o);

        var best = new double[length + 1];
        var choice = new Window?[length + 1];
        for (var o = length - 1; o >= 0; o--)
        {
            // A byte that doesn't change (or isn't payload) may stay uncovered; a changing one must be covered.
            best[o] = skip(o) || !varies[o] ? best[o + 1] : double.NegativeInfinity;
            choice[o] = null;

            foreach (var w in Windows(frames, o, length, skip, varies, counters))
            {
                var total = w.Value + best[o + w.Size];
                if (total > best[o]) { best[o] = total; choice[o] = w; }
            }
        }

        for (var o = 0; o < length;)
        {
            if (choice[o] is { } w)
            {
                result.Add(new FieldSuggestion(o, w.Type, w.Name, w.Reason, w.IsCounter));
                o += w.Size;
            }
            else o++;
        }
        return result;
    }

    private sealed record Window(int Size, string Type, string Name, string Reason, double Value, bool IsCounter = false);

    private static IEnumerable<Window> Windows(IReadOnlyList<byte[]> frames, int at, int length, Func<int, bool> skip, bool[] varies, bool[] counters)
    {
        foreach (var size in new[] { 1, 2, 4 })
        {
            if (at + size > length) continue;
            var range = Enumerable.Range(at, size).ToArray();
            if (range.Any(skip) || !range.Any(o => varies[o])) continue;
            if (size > 1 && range.Any(o => counters[o])) continue;
            var constants = range.Count(o => !varies[o]);

            if (size == 1)
            {
                var counter = counters[at];
                var j = Jitter(frames.Select(f => (double)f[at]).ToArray());
                yield return new Window(1, "u8", counter ? $"Counter{at}" : $"Byte{at}",
                    counter ? "increments by one every frame" : "one changing byte", counter ? 0.5 : 0.45 - j, counter);
            }
            else if (size == 2)
            {
                var (type, jitter) = BestInteger(frames, at, 2);
                if (jitter > 0.25 || MsbJumps(frames, at, 2, type)) continue;   // noisy, or the "high" byte jumps around: not one value
                yield return new Window(2, type, $"Word{at}", "16-bit value; byte order chosen by smoothness", 2 * (0.45 - jitter) * 1.25 - 0.08 * constants);   // coherent multi-byte values beat the same bytes read singly
            }
            else
            {
                var (intType, intJitter) = BestInteger(frames, at, 4);
                if (TryFloat32(frames, at, out var floatType, out var floatJitter, out var relativeStep) && floatJitter <= 0.25
                    && !MsbJumps(frames, at, 4, floatType))
                {
                    // A real measurement moves by a small fraction of itself; a misread float jumps by orders of magnitude.
                    var bonus = relativeStep < 0.05 ? 1.5 : 1.1;
                    yield return new Window(4, floatType, $"Float{at}", "reads as a smooth, sensible 32-bit float", 4 * (0.45 - floatJitter) * bonus - 0.08 * constants);
                }
                if (intJitter <= 0.25 && !MsbJumps(frames, at, 4, intType))
                    yield return new Window(4, intType, $"Long{at}", "32-bit integer", 4 * (0.45 - intJitter) * 1.25 - 0.08 * constants);
            }
        }
    }

    /// <summary>
    /// In a real multi-byte value the most significant byte only ever moves by one step at a time (a carry from the
    /// byte below). If it regularly jumps by two or more, the window glues together bytes of different fields.
    /// </summary>
    private static bool MsbJumps(IReadOnlyList<byte[]> frames, int at, int size, string type)
    {
        var msb = type.EndsWith("le", StringComparison.Ordinal) ? at + size - 1 : at;
        var jumps = 0;
        for (var i = 1; i < frames.Count; i++)
            if (Math.Abs(frames[i][msb] - frames[i - 1][msb]) >= 2) jumps++;
        return (double)jumps / (frames.Count - 1) >= 0.2;
    }

    private static double ChangeRate(IReadOnlyList<byte[]> frames, int o)
    {
        var changes = 0;
        for (var i = 1; i < frames.Count; i++) if (frames[i][o] != frames[i - 1][o]) changes++;
        return (double)changes / (frames.Count - 1);
    }

    private static bool IsCounter(IReadOnlyList<byte[]> frames, int o)
    {
        var steps = 0;
        for (var i = 1; i < frames.Count; i++) if ((byte)(frames[i - 1][o] + 1) == frames[i][o]) steps++;
        return steps >= (frames.Count - 1) * 0.9;
    }

    /// <summary>Mean frame-to-frame change relative to the value's overall spread: small for a real measurement, about a third for noise.</summary>
    public static double Jitter(double[] values)
    {
        double min = values.Min(), max = values.Max();
        var spread = max - min;
        if (spread <= 0) return 0;
        double delta = 0;
        for (var i = 1; i < values.Length; i++) delta += Math.Abs(values[i] - values[i - 1]);
        return delta / (values.Length - 1) / spread;
    }

    /// <summary>Picks big/little endian and signedness for a 2- or 4-byte integer: the right reading changes smoothly between frames.</summary>
    private static (string Type, double Jitter) BestInteger(IReadOnlyList<byte[]> frames, int at, int size)
    {
        var best = size == 2 ? "u16" : "u32";
        var bestJitter = double.MaxValue;
        foreach (var (name, big, signed) in new[] { ("", true, false), ("le", false, false), ("", true, true), ("le", false, true) })
        {
            var j = Jitter(frames.Select(f => Read(f, at, size, big, signed)).ToArray());
            if (signed) j *= 1.02;   // unsigned unless the signed reading is clearly smoother (values crossing zero)

            // Same smoothness either way round; the right order has the steadier byte in the most significant place.
            var msb = big ? at : at + size - 1;
            var lsb = big ? at + size - 1 : at;
            if (ChangeRate(frames, msb) > ChangeRate(frames, lsb)) j *= 1.1;
            if (j < bestJitter)
            {
                bestJitter = j;
                best = (signed ? "i" : "u") + (size * 8) + name;
            }
        }
        return (best, bestJitter);
    }

    private static double Read(byte[] f, int at, int size, bool big, bool signed)
    {
        var s = f.AsSpan(at, size);
        return size == 2
            ? signed ? (big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s))
                     : (big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s))
            : signed ? (big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s))
                     : (big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s));
    }

    private static bool TryFloat32(IReadOnlyList<byte[]> frames, int at, out string type, out double jitter, out double relativeStep)
    {
        type = "f32";
        jitter = double.MaxValue;
        relativeStep = double.MaxValue;
        var found = false;
        foreach (var (name, big) in new[] { ("f32", true), ("f32le", false) })
        {
            var values = frames.Select(f => (double)(big ? BinaryPrimitives.ReadSingleBigEndian(f.AsSpan(at, 4)) : BinaryPrimitives.ReadSingleLittleEndian(f.AsSpan(at, 4)))).ToArray();
            if (!values.All(v => double.IsFinite(v) && (v == 0 || Math.Abs(v) is > 1e-4 and < 1e9))) continue;
            var j = Jitter(values);
            if (j < jitter)
            {
                jitter = j;
                type = name;
                found = true;
                var meanAbs = values.Average(Math.Abs);
                var step = 0.0;
                for (var i = 1; i < values.Length; i++) step += Math.Abs(values[i] - values[i - 1]);
                relativeStep = meanAbs == 0 ? 0 : step / (values.Length - 1) / meanAbs;
            }
        }
        return found;
    }
}

/// <summary>Reading hex from text a person copied out of a terminal, logic analyser or datasheet.</summary>
public static class HexText
{
    /// <summary>
    /// Accepts "AA 55 01", "0xAA,0x55", "AA-55-01" and dumps with an offset column ("0010: AA 55 …").
    /// Tokens that are not exactly two hex digits are ignored, so ASCII columns don't matter much.
    /// </summary>
    public static bool TryParse(string text, out byte[] bytes)
    {
        var result = new List<byte>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon is > 0 and <= 8 && line[..colon].All(Uri.IsHexDigit)) line = line[(colon + 1)..];

            // anything after a double space or a '|' is the ASCII column of a hex dump
            var cut = line.IndexOf('|');
            if (cut >= 0) line = line[..cut];

            foreach (var token in line.Split([' ', '\t', ',', ';', '-'], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
                if (t.Length == 2 && byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) result.Add(b);
                else if (t.Length > 2 && t.Length % 2 == 0 && t.All(Uri.IsHexDigit) && !token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                         && t.Length >= 4)
                    result.AddRange(Convert.FromHexString(t));   // a run like AA550102
            }
        }
        bytes = result.ToArray();
        return bytes.Length > 0;
    }

    public static string Format(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
