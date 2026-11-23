using System.Text;

namespace RizzServer.Core;

public sealed record TagCsvResult(IReadOnlyList<TagConfig> Tags, IReadOnlyList<string> Errors);

/// <summary>
/// CSV import/export of a device's tags. Columns: Tag Name, Address, Data Type, Client Access
/// (the names used by KEPServerEX exports; common aliases and Kepware type names are accepted).
/// </summary>
public static class TagCsv
{
    private static readonly Dictionary<string, TagDataType> DataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"] = TagDataType.Boolean, ["Bool"] = TagDataType.Boolean,
        ["Int16"] = TagDataType.Int16, ["Short"] = TagDataType.Int16, ["Char"] = TagDataType.Int16,
        ["UInt16"] = TagDataType.UInt16, ["Word"] = TagDataType.UInt16, ["Byte"] = TagDataType.UInt16,
        ["Int32"] = TagDataType.Int32, ["Long"] = TagDataType.Int32,
        ["UInt32"] = TagDataType.UInt32, ["DWord"] = TagDataType.UInt32,
        ["Int64"] = TagDataType.Int64, ["LLong"] = TagDataType.Int64,
        ["UInt64"] = TagDataType.UInt64, ["QWord"] = TagDataType.UInt64,
        ["Float"] = TagDataType.Float, ["Single"] = TagDataType.Float,
        ["Double"] = TagDataType.Double,
        ["String"] = TagDataType.String,
    };

    private static readonly Dictionary<string, TagAccess> Accesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ReadOnly"] = TagAccess.ReadOnly, ["Read Only"] = TagAccess.ReadOnly, ["RO"] = TagAccess.ReadOnly, ["R"] = TagAccess.ReadOnly,
        ["ReadWrite"] = TagAccess.ReadWrite, ["Read/Write"] = TagAccess.ReadWrite, ["Read Write"] = TagAccess.ReadWrite,
        ["RW"] = TagAccess.ReadWrite, ["R/W"] = TagAccess.ReadWrite,
    };

    // ---- Export ----

    public static string Format(IEnumerable<TagConfig> tags)
    {
        var sb = new StringBuilder("Tag Name,Address,Data Type,Client Access\r\n");
        foreach (var t in tags)
            sb.Append(Escape(t.Name)).Append(',').Append(Escape(t.Address)).Append(',')
              .Append(KepwareType(t.DataType)).Append(',')
              .Append(t.Access == TagAccess.ReadWrite ? "Read/Write" : "Read Only").Append("\r\n");
        return sb.ToString();
    }

    private static string KepwareType(TagDataType t) => t switch
    {
        TagDataType.Int16 => "Short",
        TagDataType.UInt16 => "Word",
        TagDataType.Int32 => "Long",
        TagDataType.UInt32 => "DWord",
        TagDataType.Int64 => "LLong",
        TagDataType.UInt64 => "QWord",
        _ => t.ToString(),
    };

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n', ';', '\t']) >= 0 || s.StartsWith(' ') || s.EndsWith(' ')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // ---- Import ----

    public static TagCsvResult Parse(string text)
    {
        var tags = new List<TagConfig>();
        var errors = new List<string>();
        var rows = ReadRows(text.TrimStart('﻿'));
        if (rows.Count == 0) return new TagCsvResult(tags, ["The file is empty."]);

        // Header: map column names to positions.
        var header = rows[0];
        int Col(params string[] names)
        {
            for (var i = 0; i < header.Count; i++)
            {
                var key = Normalize(header[i]);
                if (names.Contains(key)) return i;
            }
            return -1;
        }

        var cName = Col("tagname", "name", "tag", "item", "itemid");
        var cAddr = Col("address", "addr");
        var cType = Col("datatype", "type");
        var cAccess = Col("clientaccess", "access", "readwrite", "rw");

        if (cName < 0 || cAddr < 0)
            return new TagCsvResult(tags, ["The header row must contain 'Tag Name' and 'Address' columns."]);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int c) => c >= 0 && c < row.Count ? row[c].Trim() : "";
            var line = r + 1;

            var name = Cell(cName);
            var address = Cell(cAddr);
            if (name.Length == 0) { errors.Add($"Line {line}: tag name is empty."); continue; }
            if (address.Length == 0) { errors.Add($"Line {line}: '{name}' has no address."); continue; }
            if (!seen.Add(name)) { errors.Add($"Line {line}: '{name}' appears more than once; later row ignored."); continue; }

            var type = TagDataType.Int16;
            var typeText = Cell(cType);
            if (cType >= 0 && typeText.Length > 0 && !DataTypes.TryGetValue(typeText, out type))
            {
                errors.Add($"Line {line}: '{name}' has unknown data type '{typeText}'.");
                continue;
            }

            var access = TagAccess.ReadOnly;
            var accessText = Cell(cAccess);
            if (cAccess >= 0 && accessText.Length > 0 && !Accesses.TryGetValue(accessText, out access))
            {
                errors.Add($"Line {line}: '{name}' has unknown client access '{accessText}'.");
                continue;
            }

            tags.Add(new TagConfig { Name = name, Address = address, DataType = type, Access = access });
        }

        if (tags.Count == 0 && errors.Count == 0) errors.Add("The file has a header but no tag rows.");
        return new TagCsvResult(tags, errors);
    }

    /// <summary>Adds new tags and updates existing ones (matched by name) in place.</summary>
    public static (int Added, int Updated) ApplyTo(DeviceConfig device, IEnumerable<TagConfig> tags)
    {
        int added = 0, updated = 0;
        foreach (var t in tags)
        {
            var existing = device.Tags.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { device.Tags.Add(t); added++; }
            else
            {
                existing.Address = t.Address;
                existing.DataType = t.DataType;
                existing.Access = t.Access;
                updated++;
            }
        }
        return (added, updated);
    }

    private static string Normalize(string header) =>
        new string(header.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>RFC 4180 reader with delimiter detection (comma, semicolon or tab) from the first line.</summary>
    private static List<List<string>> ReadRows(string text)
    {
        var delimiter = DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldWasQuoted = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldWasQuoted = false;
        }

        void EndRow()
        {
            EndField();
            // Skip rows that are completely blank.
            if (row.Count > 1 || row[0].Length > 0 || fieldWasQuoted) rows.Add(row);
            row = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { inQuotes = true; fieldWasQuoted = true; }
            else if (c == delimiter) EndField();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        int comma = 0, semi = 0, tab = 0;
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes)
            {
                if (c is '\n' or '\r') break;
                if (c == ',') comma++;
                else if (c == ';') semi++;
                else if (c == '\t') tab++;
            }
        }
        return tab > comma && tab > semi ? '\t' : semi > comma ? ';' : ',';
    }
}
