using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.Core;

/// <summary>
/// A reusable device definition: the driver, its settings, the scan rate and the tags, with
/// <c>{parameter}</c> placeholders (optionally <c>{parameter+N}</c>) in names, addresses, settings and descriptions.
/// Ten identical meters become ten <see cref="TemplateEngine.Instantiate"/> calls instead of ten hand-built devices.
/// </summary>
public partial class DeviceTemplate : ObservableObject
{
    [ObservableProperty] private string _name = "Template1";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private DriverType _driverType = DriverType.ModbusTcp;
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private string? _pluginId;
    [ObservableProperty] private int _scanRateMs = 1000;

    /// <summary>Parameter name → default value. Every <c>{name}</c> used below must be declared here.</summary>
    [JsonPropertyOrder(98)] public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Device-scoped driver settings, may contain placeholders (e.g. Host = "{host}").</summary>
    [JsonPropertyOrder(100)] public Dictionary<string, string> Settings { get; set; } = new();
    [JsonPropertyOrder(101)] public ObservableCollection<TagConfig> Tags { get; set; } = new();
}

public static class TemplateEngine
{
    private static readonly Regex Placeholder = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>[+-])(?<n>\d+))?\}", RegexOptions.Compiled);

    /// <summary>Placeholders used anywhere in the template that aren't declared as parameters.</summary>
    public static List<string> UndeclaredParameters(DeviceTemplate template)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Scan(string? text)
        {
            if (text is null) return;
            foreach (Match m in Placeholder.Matches(text)) used.Add(m.Groups["name"].Value);
        }
        foreach (var v in template.Settings.Values) Scan(v);
        foreach (var t in template.Tags) { Scan(t.Name); Scan(t.Address); Scan(t.Description); Scan(t.Units); }
        return used.Where(u => !template.Parameters.ContainsKey(u)).OrderBy(u => u, StringComparer.Ordinal).ToList();
    }

    /// <summary>Replaces placeholders. <c>{base+10}</c> adds to a numeric value. Unknown names are left alone (and reported by validation).</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(text, m =>
        {
            if (!values.TryGetValue(m.Groups["name"].Value, out var v)) return m.Value;
            if (!m.Groups["op"].Success) return v;
            if (!long.TryParse(v, out var n)) return m.Value;
            var delta = long.Parse(m.Groups["n"].Value);
            return (m.Groups["op"].Value == "+" ? n + delta : n - delta).ToString();
        });

    private static Dictionary<string, string> Resolve(DeviceTemplate template, IReadOnlyDictionary<string, string>? supplied)
    {
        var values = new Dictionary<string, string>(template.Parameters, StringComparer.Ordinal);
        if (supplied is not null) foreach (var (k, v) in supplied) values[k] = v;
        return values;
    }

    /// <summary>Builds a device from the template. The device remembers the template and the values it was built with.</summary>
    public static DeviceConfig Instantiate(DeviceTemplate template, string deviceName, IReadOnlyDictionary<string, string>? values = null)
    {
        var resolved = Resolve(template, values);
        var device = new DeviceConfig
        {
            Name = deviceName,
            ScanRateMs = template.ScanRateMs,
            Template = template.Name,
            TemplateParams = new Dictionary<string, string>(resolved),
        };
        foreach (var (k, v) in template.Settings) device.Settings[k] = Expand(v, resolved);
        foreach (var t in template.Tags) device.Tags.Add(ExpandTag(t, resolved));
        return device;
    }

    private static TagConfig ExpandTag(TagConfig source, IReadOnlyDictionary<string, string> values)
    {
        var tag = ConfigStore.Clone(source);
        tag.Name = Expand(tag.Name, values);
        tag.Address = Expand(tag.Address, values);
        if (tag.Description is not null) tag.Description = Expand(tag.Description, values);
        if (tag.Units is not null) tag.Units = Expand(tag.Units, values);
        return tag;
    }

    public sealed record SyncResult(int Added, int Updated, int Removed);

    /// <summary>
    /// Brings a device back in line with its template: tags the template defines are added or updated, tags that came
    /// from an earlier version of the template and no longer exist there are removed only when <paramref name="removeObsolete"/>.
    /// Tags the user added by hand (not in the template) are never touched by an update.
    /// </summary>
    public static SyncResult Sync(DeviceConfig device, DeviceTemplate template, bool removeObsolete = false)
    {
        var values = Resolve(template, device.TemplateParams);
        device.TemplateParams = new Dictionary<string, string>(values);
        device.Template = template.Name;
        device.ScanRateMs = template.ScanRateMs;
        foreach (var (k, v) in template.Settings) device.Settings[k] = Expand(v, values);

        int added = 0, updated = 0;
        var wanted = template.Tags.Select(t => ExpandTag(t, values)).ToList();
        foreach (var w in wanted)
        {
            var existing = device.Tags.FirstOrDefault(t => string.Equals(t.Name, w.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { device.Tags.Add(w); added++; continue; }
            device.Tags[device.Tags.IndexOf(existing)] = w;
            updated++;
        }

        var removed = 0;
        if (removeObsolete)
        {
            // A tag is obsolete when no template tag (under any parameter value) could have produced its name.
            var names = wanted.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in device.Tags.Where(t => !names.Contains(t.Name)).ToList())
            { device.Tags.Remove(t); removed++; }
        }
        return new SyncResult(added, updated, removed);
    }

    /// <summary>Makes a template out of an existing device, turning the values you name into parameters.</summary>
    public static DeviceTemplate FromDevice(ChannelConfig channel, DeviceConfig device, string templateName,
        IReadOnlyDictionary<string, string>? parameterize = null)
    {
        var t = new DeviceTemplate
        {
            Name = templateName,
            DriverType = channel.DriverType,
            PluginId = channel.PluginId,
            ScanRateMs = device.ScanRateMs,
        };
        foreach (var (k, v) in device.Settings) t.Settings[k] = v;
        foreach (var tag in device.Tags) t.Tags.Add(ConfigStore.Clone(tag));

        if (parameterize is not null)
            foreach (var (param, current) in parameterize)
            {
                t.Parameters[param] = current;
                foreach (var key in t.Settings.Keys.ToList())
                    if (t.Settings[key] == current) t.Settings[key] = "{" + param + "}";
            }
        return t;
    }

    /// <summary>Problems that would make the template unusable.</summary>
    public static void Validate(DeviceTemplate template, IEnumerable<string> otherTemplateNames, List<string> errors)
    {
        if (NameRules.Validate(template.Name, "template", otherTemplateNames) is { } e) errors.Add(e);
        var where = $"Template '{template.Name}'";
        foreach (var name in UndeclaredParameters(template))
            errors.Add($"{where}: '{{{name}}}' is used but not declared as a parameter.");

        var info = DriverCatalog.Find(template.DriverType, template.PluginId);
        if (info is null) { errors.Add($"{where}: unknown driver."); return; }
        var known = info.DeviceSettings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in template.Settings.Keys)
            if (!known.Contains(key)) errors.Add($"{where}: '{key}' is not a device-level setting of the {info.DisplayName} driver.");
    }

    /// <summary>Checks that the values a device was created with satisfy the template (all placeholders resolved).</summary>
    public static bool HasUnresolved(DeviceConfig device) =>
        device.Settings.Values.Any(v => Placeholder.IsMatch(v)) ||
        device.Tags.Any(t => Placeholder.IsMatch(t.Name) || Placeholder.IsMatch(t.Address));
}
