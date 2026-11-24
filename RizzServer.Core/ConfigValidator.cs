namespace RizzServer.Core;

/// <summary>Checks a project (or a piece of one) before it is accepted, e.g. from the REST API.</summary>
public static class ConfigValidator
{
    public const int MinScanRateMs = 10;
    public const int MaxScanRateMs = 3_600_000;

    public static List<string> Validate(GatewayConfig config)
    {
        var errors = new List<string>();
        ValidateServer(config.Server, errors);

        var seen = new List<string>();
        foreach (var channel in config.Channels)
        {
            ValidateChannel(channel, seen, errors);
            seen.Add(channel.Name);
        }

        var templates = new List<string>();
        foreach (var t in config.Templates)
        {
            TemplateEngine.Validate(t, templates, errors);
            templates.Add(t.Name);
        }
        foreach (var channel in config.Channels)
            foreach (var d in channel.Devices.Where(d => d.Template is not null))
                if (!config.Templates.Any(t => string.Equals(t.Name, d.Template, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Device '{channel.Name}.{d.Name}' refers to template '{d.Template}', which does not exist.");

        var outputs = new List<string>();
        foreach (var n in config.Northbound)
        {
            ValidateNorthbound(n, outputs, errors);
            outputs.Add(n.Name);
        }
        ValidateRedundancy(config.Redundancy, errors);
        return errors;
    }

    public static void ValidateNorthbound(NorthboundConfig output, IEnumerable<string> others, List<string> errors)
    {
        if (NameRules.Validate(output.Name, "output", others) is { } nameError) errors.Add(nameError);
        var where = $"Output '{output.Name}'";
        ValidateSettings(output.Settings, NorthboundCatalog.For(output.Kind), where, errors);
        if (output.Kind == NorthboundKind.Mqtt)
        {
            if (output.SettingInt("Port", 1883) is < 1 or > 65535) errors.Add($"{where}: port is not between 1 and 65535.");
            if (output.Setting("Host").Length == 0) errors.Add($"{where}: broker host is empty.");
            if (output.Setting("TopicTemplate", "x").Contains('#') || output.Setting("TopicTemplate", "x").Contains('+'))
                errors.Add($"{where}: a publish topic cannot contain the wildcards # or +.");
        }
        else if (!Uri.TryCreate(output.Setting("Url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add($"{where}: the URL must be an absolute http:// or https:// address.");
        if (output.SettingInt("IntervalMs", 1000) < 100) errors.Add($"{where}: the interval must be at least 100 ms.");
    }

    public static void ValidateRedundancy(RedundancySettings r, List<string> errors)
    {
        if (!r.Enabled) return;
        if (!Uri.TryCreate(r.PeerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("Redundancy: the peer URL must be an absolute http:// or https:// address.");
        if (r.HeartbeatMs < 200) errors.Add("Redundancy: the heartbeat must be at least 200 ms.");
        if (r.FailoverAfterMs < r.HeartbeatMs * 2) errors.Add("Redundancy: fail over after at least two heartbeats.");
    }

    public static void ValidateServer(ServerSettings server, List<string> errors)
    {
        if (server.Port is < 1 or > 65535) errors.Add($"Server port {server.Port} is not between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(server.ApplicationName)) errors.Add("Server application name is empty.");
        if (string.IsNullOrWhiteSpace(server.NamespaceUri)) errors.Add("Server namespace URI is empty.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in server.Users)
        {
            if (string.IsNullOrWhiteSpace(u.Username)) errors.Add("A user has an empty name.");
            else if (!names.Add(u.Username)) errors.Add($"User '{u.Username}' appears more than once.");
            if (string.IsNullOrEmpty(u.PasswordHash)) errors.Add($"User '{u.Username}' has no password.");
        }
    }

    /// <param name="otherChannelNames">Names of the channels this one must not collide with.</param>
    public static void ValidateChannel(ChannelConfig channel, IEnumerable<string> otherChannelNames, List<string> errors)
    {
        if (NameRules.Validate(channel.Name, "channel", otherChannelNames) is { } nameError) errors.Add(nameError);
        var where = $"Channel '{channel.Name}'";

        var info = DriverCatalog.For(channel);
        if (channel.DriverType == DriverType.Plugin)
        {
            if (string.IsNullOrWhiteSpace(channel.PluginId)) errors.Add($"{where}: a plugin channel needs a plugin id.");
            else if (info.Source == "missing") errors.Add($"{where}: the plugin '{channel.PluginId}' is not installed.");
        }
        ValidateSettings(channel.Settings, info.ChannelSettings, where, errors);

        if (info.MaxDevices > 0 && channel.Devices.Count > info.MaxDevices)
            errors.Add($"{where}: the {info.DisplayName} driver allows {info.MaxDevices} device(s) per channel.");

        var seen = new List<string>();
        foreach (var device in channel.Devices)
        {
            ValidateDevice(device, channel, seen, errors);
            seen.Add(device.Name);
        }
    }

    public static void ValidateDevice(DeviceConfig device, ChannelConfig channel, IEnumerable<string> otherDeviceNames, List<string> errors)
    {
        if (NameRules.Validate(device.Name, "device", otherDeviceNames) is { } nameError)
            errors.Add($"Channel '{channel.Name}': {nameError}");
        var where = $"Device '{channel.Name}.{device.Name}'";

        if (device.ScanRateMs is < MinScanRateMs or > MaxScanRateMs)
            errors.Add($"{where}: scan rate {device.ScanRateMs} ms is not between {MinScanRateMs} and {MaxScanRateMs}.");

        ValidateSettings(device.Settings, DriverCatalog.For(channel).DeviceSettings, where, errors);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in device.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Name)) { errors.Add($"{where}: a tag has an empty name."); continue; }
            if (!tags.Add(tag.Name.Trim())) errors.Add($"{where}: tag '{tag.Name}' appears more than once.");
            if (string.IsNullOrWhiteSpace(tag.Address)) errors.Add($"{where}: tag '{tag.Name}' has no address.");
            ValidateTagModel(tag, where, errors);
        }
    }

    private static void ValidateTagModel(TagConfig tag, string where, List<string> errors)
    {
        if (tag.Scale == 0) errors.Add($"{where}: tag '{tag.Name}' has a scale of 0.");
        if (tag.HasScaling && tag.DataType is not (TagDataType.Float or TagDataType.Double))
            errors.Add($"{where}: tag '{tag.Name}' is scaled, so its type must be Float or Double (an integer type would lose the decimals).");
        if (tag.EuMin is not null && tag.EuMax is not null && tag.EuMin >= tag.EuMax)
            errors.Add($"{where}: tag '{tag.Name}' has a range minimum that is not below its maximum.");
        if ((tag.EuMin is not null || tag.EuMax is not null) && tag.DataType is TagDataType.String or TagDataType.Boolean)
            errors.Add($"{where}: tag '{tag.Name}' has a range but is not numeric.");
    }

    private static void ValidateSettings(IReadOnlyDictionary<string, string> settings, IEnumerable<SettingDescriptor> allowed,
        string where, List<string> errors)
    {
        var known = allowed.ToDictionary(s => s.Key, StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            if (!known.TryGetValue(key, out var d)) { errors.Add($"{where}: unknown setting '{key}'."); continue; }
            if (d.Options is { } options && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{where}: setting '{key}' must be one of {string.Join(", ", options)}.");
        }
    }
}
