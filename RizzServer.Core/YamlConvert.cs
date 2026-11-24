using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RizzServer.Core;

/// <summary>Converts between the project's JSON form and YAML so one model serves both file formats.</summary>
internal static class YamlConvert
{
    public static string JsonToYaml(string json)
    {
        var root = JsonNode.Parse(json);
        var doc = new YamlDocument(ToYaml(root));
        var stream = new YamlStream(doc);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        // YamlStream.Save ends with a document terminator line ("...") that editors and diffs don't need.
        var text = writer.ToString().TrimEnd();
        if (text.EndsWith("...", StringComparison.Ordinal)) text = text[..^3].TrimEnd();
        return text + "\n";
    }

    private static YamlNode ToYaml(JsonNode? node)
    {
        switch (node)
        {
            case null: return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            case JsonObject o:
                var map = new YamlMappingNode();
                foreach (var (k, v) in o) map.Add(new YamlScalarNode(k), ToYaml(v));
                return map;
            case JsonArray a:
                var seq = new YamlSequenceNode();
                foreach (var item in a) seq.Add(ToYaml(item));
                return seq;
            case JsonValue v:
                var kind = v.GetValueKind();
                return kind switch
                {
                    System.Text.Json.JsonValueKind.True => Plain("true"),
                    System.Text.Json.JsonValueKind.False => Plain("false"),
                    System.Text.Json.JsonValueKind.Number => Plain(v.ToJsonString()),
                    // Strings that would read back as something else ("1", "true", "null", "") are quoted.
                    _ => Str(v.GetValue<string>()),
                };
            default: throw new NotSupportedException();
        }
    }

    private static YamlScalarNode Plain(string text) => new(text) { Style = ScalarStyle.Plain };

    private static YamlScalarNode Str(string text)
    {
        var ambiguous = text.Length == 0 || Infer(text, plain: true) is not string
                        || text.Trim() != text || text.Contains('\n') || text.StartsWith("${", StringComparison.Ordinal)
                        || text.IndexOfAny([':', '#', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`']) >= 0
                        || text.StartsWith('-') || text.StartsWith('?');
        return new YamlScalarNode(text) { Style = ambiguous ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain };
    }

    public static string YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0) throw new FormatException("The YAML file is empty.");
        return FromYaml(stream.Documents[0].RootNode)?.ToJsonString() ?? "null";
    }

    private static JsonNode? FromYaml(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(c =>
            new KeyValuePair<string, JsonNode?>(((YamlScalarNode)c.Key).Value ?? "", FromYaml(c.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(FromYaml).ToArray()),
        YamlScalarNode sc => Scalar(sc),
        _ => throw new FormatException("Unsupported YAML construct (anchors and aliases are not used in project files)."),
    };

    private static JsonNode? Scalar(YamlScalarNode node)
    {
        var text = node.Value ?? "";
        var quoted = node.Style is ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted or ScalarStyle.Literal or ScalarStyle.Folded;
        if (quoted) return JsonValue.Create(text);
        return Infer(text, plain: true) switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            _ => JsonValue.Create(text),
        };
    }

    /// <summary>What a plain (unquoted) YAML scalar means.</summary>
    private static object? Infer(string text, bool plain)
    {
        if (!plain) return text;
        if (text is "null" or "Null" or "NULL" or "~") return null;
        if (text is "true" or "True" or "TRUE") return true;
        if (text is "false" or "False" or "FALSE") return false;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !text.Contains(' ')) return d;
        return text;
    }
}
