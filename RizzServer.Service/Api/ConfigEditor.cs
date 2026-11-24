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
