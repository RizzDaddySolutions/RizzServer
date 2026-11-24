using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.Core;

public enum NorthboundKind { Mqtt, Http }

/// <summary>An output that publishes tag values to another system (an MQTT broker, an HTTP endpoint).</summary>
public partial class NorthboundConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Output1";
    [ObservableProperty] private NorthboundKind _kind = NorthboundKind.Mqtt;
    [ObservableProperty] private bool _enabled = true;
    [System.Text.Json.Serialization.JsonPropertyOrder(100)] public Dictionary<string, string> Settings { get; set; } = new();

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
    public int SettingInt(string key, int fallback) => int.TryParse(Setting(key), out var v) ? v : fallback;
    public bool SettingBool(string key, bool fallback = false) => bool.TryParse(Setting(key), out var v) ? v : fallback;
}

public static class NorthboundCatalog
{
    private static readonly string[] Bools = ["False", "True"];

    private static SettingDescriptor S(string key, string label, string def, string[]? options = null, bool secret = false) =>
        new(key, label, def, SettingScope.Channel, options, secret);

    public static SettingDescriptor[] For(NorthboundKind kind) => kind switch
    {
        NorthboundKind.Mqtt =>
        [
            S("Host", "Broker host", "localhost"),
            S("Port", "Port", "1883"),
            S("ClientId", "Client ID", "rizzserver-out"),
            S("Username", "Username", ""),
            S("Password", "Password", "", secret: true),
            S("UseTls", "Use TLS", "False", Bools),
            S("TopicTemplate", "Topic ({channel} {device} {tag} {path})", "rizzserver/{channel}/{device}/{tag}"),
            S("Format", "Payload", "Json", ["Json", "Value"]),
            S("PublishMode", "Publish", "OnChange", ["OnChange", "Interval"]),
            S("IntervalMs", "Interval (ms, Interval mode)", "5000"),
            S("Filter", "Tags (path pattern, * wildcard; ; separates)", "*"),
            S("StatusTopic", "Status topic (online/offline, last will)", "rizzserver/status"),
            S("Retain", "Retain", "True", Bools),
            S("Qos", "QoS", "1", ["0", "1", "2"]),
        ],
        _ =>
        [
            S("Url", "URL", "http://localhost:8080/ingest"),
            S("AuthHeader", "Authorization header value", "", secret: true),
            S("IntervalMs", "Send every (ms)", "5000"),
            S("OnlyChanged", "Only values that changed", "True", Bools),
            S("BatchSize", "Max values per request", "500"),
            S("Filter", "Tags (path pattern, * wildcard; ; separates)", "*"),
        ],
    };

    public static IReadOnlyList<(NorthboundKind Kind, string Name)> Kinds { get; } =
        [(NorthboundKind.Mqtt, "MQTT publisher"), (NorthboundKind.Http, "HTTP webhook (JSON batches)")];

    public static NorthboundConfig NewDefault(NorthboundKind kind, string name)
    {
        var n = new NorthboundConfig { Name = name, Kind = kind };
        foreach (var d in For(kind)) n.Settings[d.Key] = d.Default;
        return n;
    }
}

public enum RedundancyRole { Primary, Secondary }

/// <summary>
/// Two service instances watch each other. The Primary runs by default; the Secondary stays idle (its OPC UA server is up
/// with a low ServiceLevel and drivers stopped) until the Primary stops answering, then takes over. See <c>README</c>.
/// </summary>
public class RedundancySettings
{
    public bool Enabled { get; set; }
    public RedundancyRole Role { get; set; } = RedundancyRole.Primary;

    /// <summary>The other instance's API, e.g. https://plc2:5080.</summary>
    public string PeerUrl { get; set; } = "";
    /// <summary>An Admin key of the peer (stored encrypted). Needed to read its state, copy its project and ask it to hand over.</summary>
    public string PeerApiKey { get; set; } = "";

    /// <summary>For a peer with a self-signed certificate: its SHA-256 thumbprint (hex). Only that certificate is accepted.</summary>
    public string PeerCertificateThumbprint { get; set; } = "";

    public int HeartbeatMs { get; set; } = 1000;

    /// <summary>The peer counts as down after this long without a successful heartbeat.</summary>
    public int FailoverAfterMs { get; set; } = 5000;

    /// <summary>The Secondary copies the Primary's project while the Primary is healthy.</summary>
    public bool SyncConfig { get; set; } = true;

    /// <summary>When the Primary comes back, the Secondary hands control back. Off = stay until the Secondary fails.</summary>
    public bool FailBack { get; set; } = true;
}
