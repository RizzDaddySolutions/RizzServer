using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.Core;

/// <summary><see cref="Plugin"/> channels name their driver in <see cref="ChannelConfig.PluginId"/>.</summary>
public enum DriverType { ModbusTcp, ModbusRtu, Serial, Mqtt, OpcDa, Rs232Poll, BinaryFrame, OpcUa, Plugin }

public enum TagDataType { Boolean, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String }

public enum TagAccess { ReadOnly, ReadWrite }

public enum TagQuality { Bad, Uncertain, Good }

public enum ConnectionState { Stopped, Connecting, Connected, Faulted }

public partial class TagConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Tag1";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private TagDataType _dataType = TagDataType.Int16;
    [ObservableProperty] private TagAccess _access = TagAccess.ReadOnly;

    // ---- data model: optional metadata, omitted from the project file when unset ----

    /// <summary>Free text shown in OPC UA browsers and exported with the tag.</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private string? _description;

    /// <summary>Engineering unit, e.g. "°C" or "bar".</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private string? _units;

    /// <summary>Linear scaling from the raw device value: engineering = raw × Scale + Offset. Use a Float/Double tag.</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private double? _scale;
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private double? _offset;

    /// <summary>Plausible engineering range. A value outside it is served with Uncertain quality.</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private double? _euMin;
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private double? _euMax;

    [JsonIgnore] public bool HasScaling => Scale is not null or 1 || Offset is not null or 0;
}

/// <summary>One device on a channel: its own address/protocol settings, scan rate and tags.</summary>
public partial class DeviceConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Device1";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _scanRateMs = 1000;

    /// <summary>Name of the <see cref="DeviceTemplate"/> this device was created from (see <see cref="TemplateEngine"/>).</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private string? _template;

    /// <summary>The parameter values the template was applied with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyOrder(99)]
    public Dictionary<string, string>? TemplateParams { get; set; }

    /// <summary>Device-scoped driver settings (see <see cref="SettingScope.Device"/>).</summary>
    [JsonPropertyOrder(100)] public Dictionary<string, string> Settings { get; set; } = new();
    [JsonPropertyOrder(101)] public ObservableCollection<TagConfig> Tags { get; set; } = new();
}

/// <summary>A communication link (driver + shared settings such as the COM port) holding one or more devices.</summary>
public partial class ChannelConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Channel1";
    [ObservableProperty] private DriverType _driverType = DriverType.ModbusTcp;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>For <see cref="DriverType.Plugin"/> channels: the id of the plugin driver.</summary>
    [ObservableProperty] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] private string? _pluginId;

    /// <summary>Channel-scoped driver settings (see <see cref="SettingScope.Channel"/>).</summary>
    [JsonPropertyOrder(100)] public Dictionary<string, string> Settings { get; set; } = new();
    [JsonPropertyOrder(101)] public ObservableCollection<DeviceConfig> Devices { get; set; } = new();

    /// <summary>Merges channel and device settings into the configuration a driver instance runs on.</summary>
    public ConnectionConfig Flatten(DeviceConfig device)
    {
        var merged = new Dictionary<string, string>(Settings);
        foreach (var (k, v) in device.Settings) merged[k] = v;
        return new ConnectionConfig
        {
            Name = $"{Name}.{device.Name}",
            ChannelName = Name,
            DeviceName = device.Name,
            DriverType = DriverType,
            PluginId = PluginId,
            PollIntervalMs = device.ScanRateMs,
            Settings = SecretStore.ExpandReferences(merged),
            Tags = device.Tags,
        };
    }
}

/// <summary>
/// Effective, runtime-only configuration of one device (channel + device settings merged).
/// This is what drivers receive; it is never saved.
/// </summary>
public partial class ConnectionConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Connection1";
    [ObservableProperty] private DriverType _driverType = DriverType.ModbusTcp;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _pollIntervalMs = 1000;

    public string ChannelName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? PluginId { get; set; }

    public Dictionary<string, string> Settings { get; set; } = new();
    public ObservableCollection<TagConfig> Tags { get; set; } = new();

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public int SettingInt(string key, int fallback) =>
        int.TryParse(Setting(key), out var v) ? v : fallback;

    public bool SettingBool(string key, bool fallback = false) =>
        bool.TryParse(Setting(key), out var v) ? v : fallback;
}

public partial class ServerSettings : ObservableObject
{
    [ObservableProperty] private string _applicationName = "RizzServer";
    [ObservableProperty] private int _port = 4840;
    [ObservableProperty] private bool _allowNoSecurity = true;
    [ObservableProperty] private bool _autoAcceptCertificates = true;
    [ObservableProperty] private string _namespaceUri = "urn:rizzserver:data";
    [ObservableProperty] private bool _allowAnonymous = true;

    /// <summary>
    /// Only publish a tag (OPC UA, MQTT/other outputs, the API's event stream) when its value or quality changed. Unchanged polls
    /// are dropped right after the driver, which is what keeps very large projects (hundreds of thousands of tags) light when most
    /// values are static. Off by default: outputs then also see every poll, which some consumers use as a heartbeat. A tag's
    /// timestamp then means "last change".
    /// </summary>
    [ObservableProperty] private bool _publishOnChange;

    /// <summary>Accounts allowed to log in with user name + password.</summary>
    public ObservableCollection<UserAccount> Users { get; set; } = new();
}

public enum UserRole { Viewer, Operator, Admin }

public class UserAccount
{
    public string Username { get; set; } = "";
    /// <summary>What the account may do over OPC UA: Viewer reads, Operator also writes tags, Admin is the same plus (reserved for) administration.</summary>
    public UserRole Role { get; set; } = UserRole.Admin;
    /// <summary>PBKDF2 hash, "iterations.salt.hash" (base64). Never the plain password.</summary>
    public string PasswordHash { get; set; } = "";
}

public class GatewayConfig
{
    public ServerSettings Server { get; set; } = new();
    public ObservableCollection<ChannelConfig> Channels { get; set; } = new();

    /// <summary>Reusable device definitions (see <see cref="TemplateEngine"/>).</summary>
    public ObservableCollection<DeviceTemplate> Templates { get; set; } = new();

    /// <summary>Outputs that publish tag values to other systems.</summary>
    public ObservableCollection<NorthboundConfig> Northbound { get; set; } = new();

    public RedundancySettings Redundancy { get; set; } = new();
}

/// <summary>Live state of one connection.</summary>
public partial class ConnectionRuntime : ObservableObject
{
    public ConnectionRuntime(ConnectionConfig config) => Config = config;
    public ConnectionConfig Config { get; }
    public string Name => Config.Name;
    [ObservableProperty] private ConnectionState _state = ConnectionState.Stopped;
    [ObservableProperty] private string _message = "";

    /// <summary>Counters and event history for commissioning (see <see cref="DeviceDiagnostics"/>).</summary>
    public DeviceStats Stats { get; } = new();
}

/// <summary>Live value of one tag; this is what the OPC UA address space mirrors.</summary>
public partial class TagRuntime : ObservableObject
{
    public TagRuntime(ConnectionConfig connection, TagConfig config)
    {
        Connection = connection;
        Config = config;
    }

    public ConnectionConfig Connection { get; }
    public TagConfig Config { get; }
    public string ChannelName => Connection.ChannelName;
    public string DeviceName => Connection.DeviceName;
    public string NodePath => $"{ChannelName}.{DeviceName}.{Config.Name}";

    [ObservableProperty] private object? _value;
    [ObservableProperty] private TagQuality _quality = TagQuality.Bad;
    [ObservableProperty] private DateTime _timestamp = DateTime.UtcNow;
}
