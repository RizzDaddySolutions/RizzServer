using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RizzServer.Core;

public static class ConfigStore
{
    /// <summary>
    /// Where the project file, UI settings and certificates live. Defaults to the per-user application-data
    /// folder; set the <c>RIZZSERVER_DATA_DIR</c> environment variable to use another folder (portable installs, tests).
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("RIZZSERVER_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RizzServer");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GatewayConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                if (JsonNode.Parse(text) is JsonObject root && root.ContainsKey("Connections") && !root.ContainsKey("Channels"))
                    return MigrateV1(text);
                return JsonSerializer.Deserialize<GatewayConfig>(text, Options) ?? new();
            }
        }
        catch (JsonException) { /* corrupt file: fall through to defaults */ }
        return new GatewayConfig();
    }

    public static void Save(GatewayConfig config)
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    // ---- v1 (flat "Connections") → channels with one device each ----

    private sealed class LegacyConnection
    {
        public string Name { get; set; } = "Channel1";
        public DriverType DriverType { get; set; }
        public bool Enabled { get; set; } = true;
        public int PollIntervalMs { get; set; } = 1000;
        public Dictionary<string, string> Settings { get; set; } = new();
        public List<TagConfig> Tags { get; set; } = new();
    }

    private sealed class LegacyConfig
    {
        public ServerSettings Server { get; set; } = new();
        public List<LegacyConnection> Connections { get; set; } = new();
    }

    private static GatewayConfig MigrateV1(string text)
    {
        var old = JsonSerializer.Deserialize<LegacyConfig>(text, Options) ?? new();
        var config = new GatewayConfig { Server = old.Server };

        foreach (var c in old.Connections)
        {
            var info = DriverCatalog.Get(c.DriverType);
            var channelKeys = info.ChannelSettings.Select(s => s.Key).ToHashSet();

            var channel = new ChannelConfig { Name = c.Name, DriverType = c.DriverType, Enabled = c.Enabled };
            var device = new DeviceConfig { Name = "Device1", ScanRateMs = c.PollIntervalMs };
            foreach (var (key, value) in c.Settings)
                (channelKeys.Contains(key) ? channel.Settings : device.Settings)[key] = value;
            foreach (var tag in c.Tags) device.Tags.Add(tag);

            channel.Devices.Add(device);
            config.Channels.Add(channel);
        }
        return config;
    }
}
