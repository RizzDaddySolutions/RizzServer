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
        return errors;
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

        var info = DriverCatalog.Get(channel.DriverType);
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

        ValidateSettings(device.Settings, DriverCatalog.Get(channel.DriverType).DeviceSettings, where, errors);

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in device.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Name)) { errors.Add($"{where}: a tag has an empty name."); continue; }
            if (!tags.Add(tag.Name.Trim())) errors.Add($"{where}: tag '{tag.Name}' appears more than once.");
            if (string.IsNullOrWhiteSpace(tag.Address)) errors.Add($"{where}: tag '{tag.Name}' has no address.");
        }
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
