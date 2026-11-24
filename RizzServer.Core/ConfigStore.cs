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
        Converters = { new JsonStringEnumConverter(), new StringMapConverter() },
    };

    /// <summary>Project file formats: JSON (the default) and YAML, chosen by file extension.</summary>
    public enum Format { Json, Yaml }

    public static Format FormatOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" ? Format.Yaml : Format.Json;

    /// <summary>The settings maps are string → string in the model; files may write numbers and booleans unquoted (YAML does).
    /// Keys are written sorted so a project diffs cleanly in version control.</summary>
    private sealed class StringMapConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var map = new Dictionary<string, string>();
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object of settings.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                reader.Read();
                map[key] = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString()!,
                    JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonTokenType.True => "True",
                    JsonTokenType.False => "False",
                    JsonTokenType.Null => "",
                    _ => throw new JsonException($"Setting '{key}' must be a scalar value."),
                };
            }
            return map;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in value.OrderBy(p => p.Key, StringComparer.Ordinal)) writer.WriteString(k, v);
            writer.WriteEndObject();
        }
    }

    public static GatewayConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                if (JsonNode.Parse(text) is JsonObject root && root.ContainsKey("Connections") && !root.ContainsKey("Channels"))
                    return MigrateV1(text);
                var config = JsonSerializer.Deserialize<GatewayConfig>(text, Options) ?? new();
                SecretStore.UnprotectProject(config);
                return config;
            }
        }
        catch (JsonException) { /* corrupt file: fall through to defaults */ }
        return new GatewayConfig();
    }

    /// <summary>Copies a project's content into another project object (the engine and OPC UA server hold the live object, so it can't be swapped).</summary>
    public static void CopyInto(GatewayConfig live, GatewayConfig candidate)
    {
        live.Server.ApplicationName = candidate.Server.ApplicationName;
        live.Server.Port = candidate.Server.Port;
        live.Server.AllowNoSecurity = candidate.Server.AllowNoSecurity;
        live.Server.AutoAcceptCertificates = candidate.Server.AutoAcceptCertificates;
        live.Server.NamespaceUri = candidate.Server.NamespaceUri;
        live.Server.AllowAnonymous = candidate.Server.AllowAnonymous;

        live.Server.Users.Clear();
        foreach (var u in candidate.Server.Users) live.Server.Users.Add(u);
        live.Channels.Clear();
        foreach (var c in candidate.Channels) live.Channels.Add(c);
        live.Templates.Clear();
        foreach (var t in candidate.Templates) live.Templates.Add(t);
        live.Northbound.Clear();
        foreach (var n in candidate.Northbound) live.Northbound.Add(n);
        live.Redundancy = candidate.Redundancy;
    }

    /// <summary>The JSON Schema of the project file, for editor completion and CI checks.</summary>
    public static string Schema() =>
        System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(
            new JsonSerializerOptions(Options) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() },
            typeof(GatewayConfig)).ToJsonString(Options);

    /// <summary>Deep copy of a channel/device/tag via its saved form (used by Duplicate).</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    /// <summary>Saves the project with its secrets encrypted (see <see cref="SecretStore"/>).</summary>
    public static void Save(GatewayConfig config)
    {
        Directory.CreateDirectory(DataDirectory);
        var copy = Clone(config);
        SecretStore.ProtectProject(copy);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Options));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    // ---- config as code: any file, JSON or YAML ----

    public static string Serialize(GatewayConfig config, Format format = Format.Json)
    {
        var json = JsonSerializer.Serialize(config, Options);
        return format == Format.Json ? json : YamlConvert.JsonToYaml(json);
    }

    /// <summary>Parses a project from text. Throws <see cref="FormatException"/> with a readable message when it isn't one.</summary>
    public static GatewayConfig Deserialize(string text, Format format = Format.Json)
    {
        try
        {
            var json = format == Format.Json ? text : YamlConvert.YamlToJson(text);
            var config = JsonSerializer.Deserialize<GatewayConfig>(json, Options)
                         ?? throw new FormatException("The project is empty.");
            SecretStore.UnprotectProject(config);
            return config;
        }
        catch (JsonException ex) { throw new FormatException(ex.Message, ex); }
        catch (YamlDotNet.Core.YamlException ex) { throw new FormatException(ex.Message, ex); }
    }

    public static GatewayConfig LoadFile(string path) => Deserialize(File.ReadAllText(path), FormatOf(path));

    /// <summary>
    /// Writes the project as a file meant for version control. Secrets are replaced by <c>${env:NAME}</c> references
    /// (listed in <paramref name="secretReferences"/> so the caller can tell the user which variables to set) unless
    /// <paramref name="includeSecrets"/> is set, in which case they are written encrypted.
    /// </summary>
    public static void SaveFile(string path, GatewayConfig config, bool includeSecrets, out List<string> secretReferences) =>
        File.WriteAllText(path, ExportText(config, FormatOf(path), includeSecrets, out secretReferences));

    /// <summary>The text of <see cref="SaveFile"/>.</summary>
    public static string ExportText(GatewayConfig config, Format format, bool includeSecrets, out List<string> secretReferences)
    {
        var copy = Clone(config);
        secretReferences = [];
        if (includeSecrets) SecretStore.ProtectProject(copy);
        else ReplaceSecretsWithReferences(copy, secretReferences);
        return Serialize(copy, format);
    }

    private static void ReplaceSecretsWithReferences(GatewayConfig config, List<string> variables)
    {
        static string Var(params string[] parts) =>
            "RIZZ_" + string.Join("_", parts.Select(p => new string(p.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())));

        void Replace(Dictionary<string, string> settings, IEnumerable<SettingDescriptor> descriptors, params string[] scope)
        {
            foreach (var d in descriptors.Where(d => d.Secret))
            {
                if (!settings.TryGetValue(d.Key, out var v) || v.Length == 0 || SecretStore.IsReference(v)) continue;
                var name = Var([.. scope, d.Key]);
                settings[d.Key] = "${env:" + name + "}";
                variables.Add(name);
            }
        }

        foreach (var channel in config.Channels)
        {
            var info = DriverCatalog.For(channel);
            Replace(channel.Settings, info.ChannelSettings, channel.Name);
            foreach (var d in channel.Devices) Replace(d.Settings, info.DeviceSettings, channel.Name, d.Name);
        }
        foreach (var n in config.Northbound) Replace(n.Settings, NorthboundCatalog.For(n.Kind), "OUT", n.Name);
        if (config.Redundancy.PeerApiKey.Length > 0 && !SecretStore.IsReference(config.Redundancy.PeerApiKey))
        {
            config.Redundancy.PeerApiKey = "${env:RIZZ_PEER_API_KEY}";
            variables.Add("RIZZ_PEER_API_KEY");
        }
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
