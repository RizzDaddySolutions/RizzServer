using Microsoft.Extensions.Logging;

namespace RizzServer.Sdk;

/// <summary>
/// The contract between RizzServer and a driver plugin. It is deliberately small and has no dependency on the server's
/// internals, so a plugin built against one SDK version keeps working while the server changes.
/// <para>
/// A plugin is a class library with one public class implementing <see cref="IDriverPlugin"/> (parameterless constructor).
/// Put the build output in <c>&lt;data folder&gt;/plugins/&lt;name&gt;/</c> (or <c>plugins/&lt;name&gt;/</c> next to the program).
/// The driver then appears in the driver list of channels, with the settings you describe.
/// </para>
/// </summary>
public static class SdkInfo
{
    /// <summary>Bumped only for breaking changes. The loader refuses plugins built for a newer contract.</summary>
    public const int ContractVersion = 1;
}

/// <summary>The type a tag's value has when served over OPC UA.</summary>
public enum SdkDataType { Boolean, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String }

public enum SdkQuality { Bad, Uncertain, Good }

public enum SettingLevel
{
    /// <summary>Shared by every device on the channel (a COM port, a broker address).</summary>
    Channel,
    /// <summary>Specific to one device (a unit id, an IP address).</summary>
    Device,
}

/// <summary>One setting shown in the channel or device form.</summary>
public sealed record SettingInfo(string Key, string Label, string Default, SettingLevel Level, string[]? Options = null, bool Secret = false);

/// <summary>A tag the driver has to read (and perhaps write).</summary>
public sealed record SdkTag(string Name, string Address, SdkDataType DataType, bool Writable);

/// <summary>What a plugin tells the server about itself.</summary>
public sealed class PluginDescriptor
{
    /// <summary>Stable key stored in projects. Lower-case letters, digits, dots and dashes: "acme.weighbridge". Never change it.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>One or two sentences on the address syntax of tags, shown in the tag editor.</summary>
    public string AddressHint { get; init; } = "";

    public string Version { get; init; } = "1.0.0";
    public IReadOnlyList<SettingInfo> Settings { get; init; } = [];

    /// <summary>Maximum devices per channel (0 = unlimited). Use 1 for drivers that own an exclusive resource such as a serial port.</summary>
    public int MaxDevices { get; init; }
}

/// <summary>Everything a driver instance needs; one instance serves one device.</summary>
public sealed class DriverContext
{
    public required string Name { get; init; }

    /// <summary>Channel and device settings merged (device wins). Secrets are already decrypted.</summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public required IReadOnlyList<SdkTag> Tags { get; init; }

    /// <summary>The device's scan rate.</summary>
    public required TimeSpan PollInterval { get; init; }

    public required ILogger Log { get; init; }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public int SettingInt(string key, int fallback) => int.TryParse(Setting(key), out var v) ? v : fallback;

    public double SettingDouble(string key, double fallback) =>
        double.TryParse(Setting(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool SettingBool(string key, bool fallback = false) => bool.TryParse(Setting(key), out var v) ? v : fallback;
}

/// <summary>The entry point of a plugin assembly.</summary>
public interface IDriverPlugin
{
    PluginDescriptor Describe();

    /// <summary>Called once per device when the runtime starts.</summary>
    PluginDriver Create(DriverContext context);
}

/// <summary>
/// Base class for a driver. The server runs the loop: <see cref="ConnectAsync"/>, then <see cref="PollAsync"/> at the scan rate; an
/// exception from either marks the device faulted (tags Bad), calls <see cref="DisconnectAsync"/> and retries after a pause.
/// Drivers that are told about changes (events, subscriptions) call <see cref="Publish(SdkTag, object?, SdkQuality)"/> whenever
/// they like and use <see cref="PollAsync"/> only as a health check.
/// </summary>
public abstract class PluginDriver(DriverContext context)
{
    private Action<SdkTag, object?, SdkQuality>? _sink;

    protected DriverContext Context { get; } = context;

    /// <summary>Open the connection. Throw to report why it failed (the message is shown to the user).</summary>
    protected abstract Task ConnectAsync(CancellationToken ct);

    /// <summary>Read the tags and <see cref="Publish(SdkTag, object?, SdkQuality)"/> each value.</summary>
    protected abstract Task PollAsync(CancellationToken ct);

    protected virtual Task DisconnectAsync() => Task.CompletedTask;

    /// <summary>Write a value to the device. The value has the tag's data type. Override for writable tags.</summary>
    protected virtual Task WriteAsync(SdkTag tag, object value, CancellationToken ct) =>
        throw new NotSupportedException($"The {GetType().Name} driver does not support writes.");

    protected void Publish(SdkTag tag, object? value, SdkQuality quality = SdkQuality.Good) => _sink?.Invoke(tag, value, quality);

    protected void Publish(string tagName, object? value, SdkQuality quality = SdkQuality.Good)
    {
        var tag = Context.Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (tag is not null) Publish(tag, value, quality);
    }

    // ---- host side (not for plugin authors) ----
    public void Attach(Action<SdkTag, object?, SdkQuality> sink) => _sink = sink;
    public Task HostConnectAsync(CancellationToken ct) => ConnectAsync(ct);
    public Task HostPollAsync(CancellationToken ct) => PollAsync(ct);
    public Task HostDisconnectAsync() => DisconnectAsync();
    public Task HostWriteAsync(SdkTag tag, object value, CancellationToken ct) => WriteAsync(tag, value, ct);
}
