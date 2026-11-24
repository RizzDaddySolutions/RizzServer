using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// How the text drivers (RS-232 text and command/response) pick a value out of a line:
/// a field index, <c>line</c> (the whole line) or <c>re:pattern</c> (first capture group). One implementation,
/// shared with the Protocol Designer, so what the designer previews is what the driver publishes.
/// </summary>
public static class TextExtract
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <returns>The extracted text, or null when the line doesn't match.</returns>
    public static string? Extract(string line, string spec, string separator)
    {
        spec = spec.Trim();
        if (spec.Length == 0 || spec.Equals("line", StringComparison.OrdinalIgnoreCase)) return line.Trim();

        if (spec.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var m = Regex.Match(line, spec[3..], RegexOptions.None, RegexTimeout);
                return !m.Success ? null : m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                return null;   // a bad or runaway pattern must never take the driver down
            }
        }

        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var fields = separator.Length == 0 ? [line] : line.Split(separator);
            return index < fields.Length ? fields[index].Trim() : null;
        }
        return null;
    }
}

/// <summary>A value the analysis found in text lines.</summary>
public sealed record TextFieldSuggestion(string Name, string Spec, TagDataType DataType, string Reason, string Sample);

/// <summary>Works out line structure of a text protocol: terminator, separator and which parts carry values.</summary>
public static class TextAnalysis
{
    private const string Number = @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?";
    private static readonly Regex NumberRegex = new(Number, RegexOptions.Compiled);
    private static readonly string[] SeparatorCandidates = [",", ";", "\t", "|", " ", ":", "="];

    // ---- escapes (the same notation the driver settings use) ----

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '\u007f': sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        return sb.ToString();
    }

    public static string Unescape(string s) =>
        Regex.Replace(s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t"),
            @"\\x([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    // ---- terminator ----

    /// <returns>"\r\n", "\n" or "\r" (the actual characters), or "" when the data has no repeated line ending.</returns>
    public static string DetectTerminator(ReadOnlySpan<byte> raw)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n') { crlf++; i++; }
            else if (raw[i] == '\n') lf++;
            else if (raw[i] == '\r') cr++;
        }
        var best = Math.Max(crlf, Math.Max(lf, cr));
        if (best < 2) return "";
        return crlf == best ? "\r\n" : lf == best ? "\n" : "\r";
    }

    /// <summary>Splits a captured stream into lines; the last, unterminated piece is returned in <paramref name="rest"/>.</summary>
    public static List<string> SplitLines(string text, string terminator, out string rest)
    {
        rest = text;
        var lines = new List<string>();
        if (terminator.Length == 0) return lines;
        int idx;
        while ((idx = rest.IndexOf(terminator, StringComparison.Ordinal)) >= 0)
        {
            lines.Add(rest[..idx]);
            rest = rest[(idx + terminator.Length)..];
        }
        return lines;
    }

    /// <summary>Bytes as text the way a terminal shows them: one byte, one character.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    // ---- separator ----

    /// <returns>The separator that splits most lines into the same number of parts, or "" when none does.</returns>
    public static string DetectSeparator(IReadOnlyList<string> lines)
    {
        var sample = lines.Select(l => l.Trim('\r', '\n')).Where(l => l.Length > 0).Take(200).ToList();
        if (sample.Count < 3) return "";

        string best = "";
        double bestScore = 0;
        foreach (var sep in SeparatorCandidates)
        {
            var counts = sample.Select(l => l.Split(sep).Length).GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            if (counts.Key < 2) continue;
            var share = (double)counts.Count() / sample.Count;
            if (share < 0.8) continue;
            // More parts is better, but a lone space splitting padded numbers is a weak signal: prefer real delimiters.
            var score = share * Math.Min(counts.Key, 6) * (sep == " " ? 0.6 : 1);
            if (score > bestScore) { bestScore = score; best = sep; }
        }
        return best;
    }

    // ---- fields ----

    public static int CountNumbers(string text) => NumberRegex.Matches(text).Count;

    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static TagDataType GuessType(IEnumerable<string> samples)
    {
        var list = samples.Select(s => s.Trim()).ToList();
        if (list.All(s => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
            return list.All(s => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) ? TagDataType.Int32 : TagDataType.Int64;
        return list.All(s => TryNumber(s, out _)) ? TagDataType.Double : TagDataType.String;
    }

    /// <summary>Suggests fields for the varying parts of similar lines (all replies to one command, or an unsolicited stream).</summary>
    public static List<TextFieldSuggestion> SuggestFields(IReadOnlyList<string> allLines, string separator)
    {
        var lines = allLines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(300).ToList();
        var result = new List<TextFieldSuggestion>();
        if (lines.Count < 3) return result;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Unique(string name)
        {
            var n = name; var i = 2;
            while (!names.Add(n)) n = $"{name}{i++}";
            return n;
        }

        // 1) separator columns: the same number of parts on (nearly) every line
        var handledByColumns = false;
        if (separator.Length > 0)
        {
            var parts = lines.Select(l => l.Split(separator)).ToList();
            var modal = parts.GroupBy(p => p.Length).OrderByDescending(g => g.Count()).First();
            if (modal.Key >= 2 && (double)modal.Count() / lines.Count >= 0.9)
            {
                handledByColumns = true;
                var rows = parts.Where(p => p.Length == modal.Key).ToList();
                var numberIndexSoFar = 0;   // how many numbers precede this column on the line (for regex fallbacks)
                for (var col = 0; col < modal.Key; col++)
                {
                    var tokens = rows.Select(r => r[col].Trim()).ToList();
                    var numbersInToken = NumberRegex.Matches(tokens[0]).Count;

                    if (tokens.Distinct().Count() > 1)
                    {
                        if (tokens.All(t => TryNumber(t, out _)))
                        {
                            // In "T=23.5 H=45" the label sits at the end of the previous column.
                            var label = col > 0 && separator is "=" or ":"
                                ? Regex.Match(rows[0][col - 1], @"([A-Za-z_][A-Za-z0-9_]*)\s*$") : Match.Empty;
                            result.Add(new TextFieldSuggestion(Unique(label.Success ? label.Groups[1].Value : $"Value{col}"),
                                col.ToString(CultureInfo.InvariantCulture), GuessType(tokens), "numeric column", tokens[^1]));
                        }
                        else if (tokens.All(t => NumberRegex.Matches(t).Count == numbersInToken) && numbersInToken >= 1)
                        {
                            // "12.34 kg", "23.5C": the number is only part of the column, so a pattern is needed
                            for (var k = 0; k < numbersInToken; k++)
                                if (NumberFieldAt(lines, numberIndexSoFar + k, "column with a unit", Unique) is { } f) result.Add(f);
                        }
                        else if (tokens.Distinct().Count() <= 6)
                            result.Add(new TextFieldSuggestion(Unique($"Status{col}"), col.ToString(CultureInfo.InvariantCulture), TagDataType.String,
                                $"{tokens.Distinct().Count()} different words", tokens[^1]));
                    }
                    numberIndexSoFar += numbersInToken;
                }
            }
        }

        // 2) no usable columns: find the numbers themselves
        if (!handledByColumns)
        {
            var counts = lines.Select(l => NumberRegex.Matches(l).Count).GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            if (counts.Key >= 1 && (double)counts.Count() / lines.Count >= 0.9)
                for (var k = 0; k < counts.Key; k++)
                    if (NumberFieldAt(lines.Where(l => NumberRegex.Matches(l).Count == counts.Key).ToList(), k, "number in the line", Unique) is { } f)
                        result.Add(f);
        }
        return result;
    }

    /// <summary>A field for the k-th number (0-based) of each line, found with a pattern anchored on the text before it.</summary>
    public static TextFieldSuggestion? SuggestNumberField(IReadOnlyList<string> lines, int k, string reason = "number in the line") =>
        NumberFieldAt(lines.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(300).ToList(), k, reason, n => n);

    private static TextFieldSuggestion? NumberFieldAt(IReadOnlyList<string> lines, int k, string reason, Func<string, string> unique)
    {
        var withNumbers = lines.Where(l => NumberRegex.Matches(l).Count > k).ToList();
        if (withNumbers.Count < 3) return null;

        var expected = withNumbers.Select(l => NumberRegex.Matches(l)[k].Value).ToList();
        if (expected.Distinct().Count() <= 1) return null;   // constant: not a measurement

        var reference = withNumbers[^1];
        var match = NumberRegex.Matches(reference)[k];

        string? spec = null;
        foreach (var prefixLength in new[] { 0, 2, 3, 4, 6, 8, 12 })
        {
            if (prefixLength > match.Index) break;
            var pattern = LiteralWithSpaces(reference.Substring(match.Index - prefixLength, prefixLength)) + "(" + Number + ")";
            if (Agrees(withNumbers, pattern, expected)) { spec = "re:" + pattern; break; }
        }
        // Anchoring on text failed (the text before varies): count numbers from the start of the line instead.
        spec ??= "re:^" + (k == 0 ? "" : $"(?:\\D*{Number}){{{k}}}") + @"\D*(" + Number + ")";
        if (!Agrees(withNumbers, spec[3..], expected)) return null;

        var label = Regex.Match(reference[..match.Index], @"([A-Za-z_][A-Za-z0-9_]*)\s*[=:]?\s*$");
        var name = label.Success ? label.Groups[1].Value : $"Value{k + 1}";
        return new TextFieldSuggestion(unique(name), spec, GuessType(expected), reason, expected[^1]);
    }

    private static bool Agrees(IReadOnlyList<string> lines, string pattern, IReadOnlyList<string> expected)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            var ok = 0;
            for (var i = 0; i < lines.Count; i++)
                if (regex.Match(lines[i]) is { Success: true } m && m.Groups.Count > 1 && m.Groups[1].Value == expected[i]) ok++;
            return ok >= lines.Count * 0.95;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return false; }
    }

    private static string LiteralWithSpaces(string text) =>
        Regex.Replace(Regex.Escape(text), @"(\\ |\s)+", @"\s*");
}
