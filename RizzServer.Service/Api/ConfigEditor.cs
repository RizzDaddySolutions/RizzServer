using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;

namespace RizzServer.Service.Api;

/// <summary>
/// Applies a configuration change safely: on a copy, validated as a whole, then committed and saved.
/// A rejected change leaves the live project untouched. Changes are refused while the runtime runs,
/// exactly like the desktop UI locks editing.
/// </summary>
public sealed class ConfigEditor(GatewayRuntime runtime)
{
    public const string SecretMask = "********";

    public async Task<IResult> EditAsync(Func<GatewayConfig, IResult?> apply, Func<GatewayConfig, IResult> success)
    {
        await runtime.Gate.WaitAsync();
        try
        {
            if (runtime.SourceFile is { } file)
                return Json(409, new ErrorBody($"The project is managed by the file '{file}' (--config). Change the file; the service reloads it."));
            if (runtime.IsRunning)
                return Json(409, new ErrorBody("The runtime is running. POST /api/v1/runtime/stop, change the configuration, then start it again."));

            var candidate = ConfigStore.Clone(runtime.Config);
            if (apply(candidate) is { } failure) return failure;

            var errors = ConfigValidator.Validate(candidate);
            if (errors.Count > 0) return Json(400, new ErrorBody("The configuration is not valid.", errors));

            runtime.ReplaceConfig(candidate);
            runtime.Save();
            return success(runtime.Config);
        }
        finally { runtime.Gate.Release(); }
    }

    public static IResult Json(int status, object body) => Results.Json(body, statusCode: status);
    public static IResult NotFound(string what) => Json(404, new ErrorBody($"{what} not found."));

    public static ChannelConfig? FindChannel(GatewayConfig cfg, string name) =>
        cfg.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static DeviceConfig? FindDevice(ChannelConfig channel, string name) =>
        channel.Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---- secrets: passwords never leave the service; sending the mask back keeps the stored value ----

    public static ChannelConfig Mask(ChannelConfig channel)
    {
        var copy = ConfigStore.Clone(channel);
        var info = DriverCatalog.For(copy);
        MaskSettings(copy.Settings, info.ChannelSettings);
        foreach (var d in copy.Devices) MaskSettings(d.Settings, info.DeviceSettings);
        return copy;
    }

    /// <summary>Counts instead of tags: cheap even for a channel with hundreds of thousands of tags (no deep copy).</summary>
    public static ChannelSummary Summarize(ChannelConfig channel)
    {
        var settings = new Dictionary<string, string>(channel.Settings);
        MaskSettings(settings, DriverCatalog.For(channel).ChannelSettings);
        return new(channel.Name, channel.DriverType, channel.Enabled, channel.PluginId, settings,
            channel.Devices.Count, channel.Devices.Sum(d => d.Tags.Count));
    }

    public static DeviceSummary Summarize(ChannelConfig channel, DeviceConfig device)
    {
        var settings = new Dictionary<string, string>(device.Settings);
        MaskSettings(settings, DriverCatalog.For(channel).DeviceSettings);
        return new(device.Name, device.Enabled, device.ScanRateMs, device.Template, settings, device.Tags.Count);
    }

    /// <summary>One device with secrets masked (copies only that device, not the whole channel).</summary>
    public static DeviceConfig Mask(ChannelConfig channel, DeviceConfig device)
    {
        var copy = ConfigStore.Clone(device);
        MaskSettings(copy.Settings, DriverCatalog.For(channel).DeviceSettings);
        return copy;
    }

    /// <summary>
    /// Optional paging for list endpoints: <c>skip</c> and <c>take</c> (1–<see cref="MaxPage"/>). Without them the whole list is
    /// returned, as before. The total is always sent in the <c>X-Total-Count</c> header.
    /// </summary>
    public const int MaxPage = 10_000;

    public static IResult Page<T>(HttpContext http, IReadOnlyList<T> all, int? skip, int? take, Func<T, object> project)
    {
        if (skip is < 0) return Json(400, new ErrorBody("skip must be 0 or more."));
        if (take is < 1 or > MaxPage) return Json(400, new ErrorBody($"take must be between 1 and {MaxPage}."));
        http.Response.Headers["X-Total-Count"] = all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var start = Math.Min(skip ?? 0, all.Count);
        var count = Math.Min(take ?? all.Count, all.Count - start);
        var page = new List<object>(count);
        for (var i = start; i < start + count; i++) page.Add(project(all[i]));
        return Results.Ok(page);
    }

    public static NorthboundConfig Mask(NorthboundConfig output)
    {
        var copy = ConfigStore.Clone(output);
        MaskSettings(copy.Settings, NorthboundCatalog.For(copy.Kind));
        return copy;
    }

    public static void RestoreSecrets(NorthboundConfig incoming, NorthboundConfig? existing) =>
        Restore(incoming.Settings, existing?.Settings, NorthboundCatalog.For(incoming.Kind));

    public static RedundancySettings Mask(RedundancySettings r)
    {
        var copy = ConfigStore.Clone(r);
        if (copy.PeerApiKey.Length > 0) copy.PeerApiKey = SecretMask;
        return copy;
    }

    private static void MaskSettings(Dictionary<string, string> settings, IEnumerable<SettingDescriptor> descriptors)
    {
        foreach (var d in descriptors.Where(d => d.Secret))
            if (settings.TryGetValue(d.Key, out var v) && v.Length > 0) settings[d.Key] = SecretMask;
    }

    /// <summary>Replaces any "********" in <paramref name="incoming"/> with the value stored in <paramref name="existing"/>.</summary>
    public static void RestoreSecrets(ChannelConfig incoming, ChannelConfig? existing)
    {
        var info = DriverCatalog.For(incoming);
        Restore(incoming.Settings, existing?.Settings, info.ChannelSettings);
        foreach (var d in incoming.Devices)
            Restore(d.Settings, existing is null ? null : FindDevice(existing, d.Name)?.Settings, info.DeviceSettings);
    }

    private static void Restore(Dictionary<string, string> incoming, Dictionary<string, string>? existing, IEnumerable<SettingDescriptor> descriptors)
    {
        foreach (var d in descriptors.Where(d => d.Secret))
        {
            if (!incoming.TryGetValue(d.Key, out var v) || v != SecretMask) continue;
            if (existing is not null && existing.TryGetValue(d.Key, out var old)) incoming[d.Key] = old;
            else incoming.Remove(d.Key);
        }
    }
}
