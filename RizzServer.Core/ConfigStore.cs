using System.Text.Json;
using System.Text.Json.Serialization;

namespace RizzServer.Core;

public static class ConfigStore
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RizzServer");

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
                return JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(ConfigPath), Options) ?? new();
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
}
