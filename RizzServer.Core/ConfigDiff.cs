using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RizzServer.Core;

public enum DiffKind { Added, Removed, Changed }

public sealed record DiffEntry(DiffKind Kind, string Path, string? Old, string? New)
{
    public override string ToString() => Kind switch
    {
        DiffKind.Added => $"+ {Path}" + (New is null ? "" : $" = {New}"),
        DiffKind.Removed => $"- {Path}" + (Old is null ? "" : $" = {Old}"),
        _ => $"~ {Path}: {Old} -> {New}",
    };
}

/// <summary>
/// Compares two projects by meaning, not by text: order of settings and formatting don't matter, and the result names
/// channels, devices and tags the way people do (<c>Plant.PLC1.Temp</c>). Secret values are never printed.
/// </summary>
public static class ConfigDiff
{
    public static List<DiffEntry> Compare(GatewayConfig before, GatewayConfig after)
    {
        var a = Flatten(before);
        var b = Flatten(after);
        var result = new List<DiffEntry>();

        foreach (var (path, old) in a)
        {
            if (!b.TryGetValue(path, out var now)) result.Add(new DiffEntry(DiffKind.Removed, path, old, null));
            else if (old != now) result.Add(new DiffEntry(DiffKind.Changed, path, old, now));
        }
        foreach (var (path, now) in b)
            if (!a.ContainsKey(path)) result.Add(new DiffEntry(DiffKind.Added, path, null, now));

        // A whole channel/device/tag appearing or vanishing is one line, not one per property.
        var parents = result.Where(e => e.Kind != DiffKind.Changed && IsContainer(e.Path)).ToList();
        result.RemoveAll(e => parents.Any(p => p.Kind == e.Kind && p.Path != e.Path && Covers(p.Path, e.Path)));
        return [.. result.OrderBy(e => e.Path, StringComparer.Ordinal)];
    }

    private static bool IsContainer(string path) => !path.Contains('.') || path.StartsWith("server.", StringComparison.Ordinal) is false && path.Count(c => c == '.') <= 2 && ContainerKind(path);

    private static bool ContainerKind(string path) =>
        path.StartsWith("channel ", StringComparison.Ordinal) && !path.Contains(".setting.") ||
        path.StartsWith("device ", StringComparison.Ordinal) && path.Count(c => c == '.') == 1 ||
        path.StartsWith("tag ", StringComparison.Ordinal) && path.Count(c => c == '.') == 2 ||
        path.StartsWith("template ", StringComparison.Ordinal) && !path.Contains(".setting.") && !path.Contains(".parameter.") ||
        path.StartsWith("output ", StringComparison.Ordinal) && !path.Contains(".setting.");

    private static bool Covers(string parent, string child)
    {
        if (child.StartsWith(parent + ".", StringComparison.Ordinal)) return true;
        // Removing channel "C" also covers "device C.D…" and "tag C.D.T…".
        if (parent.StartsWith("channel ", StringComparison.Ordinal))
        {
            var name = parent["channel ".Length..];
            return child.StartsWith("device " + name + ".", StringComparison.Ordinal) || child.StartsWith("tag " + name + ".", StringComparison.Ordinal);
        }
        if (parent.StartsWith("device ", StringComparison.Ordinal))
            return child.StartsWith("tag " + parent["device ".Length..] + ".", StringComparison.Ordinal);
        return false;
    }

    /// <summary>One line per leaf value: "channel Plant" → "", "device Plant.PLC1.ScanRateMs" → "500", and so on.</summary>
    private static SortedDictionary<string, string> Flatten(GatewayConfig config)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        void Leaves(string prefix, object obj, Func<string, string?> mask)
        {
            var node = JsonSerializer.SerializeToNode(obj, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            if (node is JsonObject o)
                foreach (var (k, v) in o)
                {
                    if (v is JsonObject or JsonArray) continue;
                    var text = Text(v);
                    map[$"{prefix}.{k}"] = mask(k) is { } masked && text.Length > 0 ? masked : text;
                }
        }

        static string Text(JsonNode? v) => v switch
        {
            null => "",
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            JsonValue jv => jv.ToJsonString(),
            _ => "",
        };

        Leaves("server", config.Server, _ => null);
        foreach (var u in config.Server.Users) { map[$"server.user.{u.Username}"] = u.Role.ToString(); map[$"server.user.{u.Username}.password"] = Fingerprint(u.PasswordHash); }

        foreach (var ch in config.Channels)
        {
            var info = DriverCatalog.For(ch);
            map[$"channel {ch.Name}"] = $"{ch.DriverType}{(ch.PluginId is null ? "" : ":" + ch.PluginId)}{(ch.Enabled ? "" : " (disabled)")}";
            Settings($"channel {ch.Name}.setting", ch.Settings, info.ChannelSettings);
            foreach (var d in ch.Devices)
            {
                map[$"device {ch.Name}.{d.Name}"] = d.Enabled ? "" : "(disabled)";
                map[$"device {ch.Name}.{d.Name}.ScanRateMs"] = d.ScanRateMs.ToString(CultureInfo.InvariantCulture);
                if (d.Template is not null) map[$"device {ch.Name}.{d.Name}.Template"] = d.Template;
                Settings($"device {ch.Name}.{d.Name}.setting", d.Settings, info.DeviceSettings);
                foreach (var t in d.Tags)
                {
                    map[$"tag {ch.Name}.{d.Name}.{t.Name}"] = "";
                    Leaves($"tag {ch.Name}.{d.Name}.{t.Name}", t, _ => null);
                    map.Remove($"tag {ch.Name}.{d.Name}.{t.Name}.Name");
                }
            }
        }

        foreach (var t in config.Templates)
        {
            map[$"template {t.Name}"] = $"{t.DriverType}{(t.PluginId is null ? "" : ":" + t.PluginId)}";
            map[$"template {t.Name}.ScanRateMs"] = t.ScanRateMs.ToString(CultureInfo.InvariantCulture);
            foreach (var (k, v) in t.Parameters) map[$"template {t.Name}.parameter.{k}"] = v;
            foreach (var (k, v) in t.Settings) map[$"template {t.Name}.setting.{k}"] = v;
            foreach (var tag in t.Tags)
            {
                map[$"template {t.Name}.tag {tag.Name}"] = "";
                Leaves($"template {t.Name}.tag {tag.Name}", tag, _ => null);
                map.Remove($"template {t.Name}.tag {tag.Name}.Name");
            }
        }

        foreach (var n in config.Northbound)
        {
            map[$"output {n.Name}"] = $"{n.Kind}{(n.Enabled ? "" : " (disabled)")}";
            Settings($"output {n.Name}.setting", n.Settings, NorthboundCatalog.For(n.Kind));
        }

        Leaves("redundancy", config.Redundancy, k => k == nameof(RedundancySettings.PeerApiKey) ? "********" : null);
        if (config.Redundancy.PeerApiKey.Length > 0)
            map["redundancy.PeerApiKey"] = Fingerprint(SecretStore.Resolve(config.Redundancy.PeerApiKey));

        return map;

        void Settings(string prefix, Dictionary<string, string> settings, IEnumerable<SettingDescriptor> descriptors)
        {
            var secrets = descriptors.Where(d => d.Secret).Select(d => d.Key).ToHashSet();
            foreach (var (k, v) in settings)
            {
                // Two different passwords must show as a change without printing either.
                string Show()
                {
                    try { return Fingerprint(SecretStore.Resolve(v)); }
                    catch (InvalidOperationException) { return Fingerprint(v); }
                }
                map[$"{prefix}.{k}"] = secrets.Contains(k) && v.Length > 0 ? Show() : v;
            }
        }
    }

    private static string Fingerprint(string secret) =>
        "secret:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))[..8].ToLowerInvariant();
}
