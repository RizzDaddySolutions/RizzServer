using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.Core;

public enum DriverType { ModbusTcp, ModbusRtu, Serial, Mqtt, OpcDa, Rs232Poll, BinaryFrame, OpcUa }

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
}

/// <summary>One device on a channel: its own address/protocol settings, scan rate and tags.</summary>
public partial class DeviceConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Device1";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _scanRateMs = 1000;

    /// <summary>Device-scoped driver settings (see <see cref="SettingScope.Device"/>).</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
    public ObservableCollection<TagConfig> Tags { get; set; } = new();
}

/// <summary>A communication link (driver + shared settings such as the COM port) holding one or more devices.</summary>
public partial class ChannelConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Channel1";
    [ObservableProperty] private DriverType _driverType = DriverType.ModbusTcp;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Channel-scoped driver settings (see <see cref="SettingScope.Channel"/>).</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
    public ObservableCollection<DeviceConfig> Devices { get; set; } = new();

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
            PollIntervalMs = device.ScanRateMs,
            Settings = merged,
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

    /// <summary>Accounts allowed to log in with user name + password.</summary>
    public ObservableCollection<UserAccount> Users { get; set; } = new();
}

public class UserAccount
{
    public string Username { get; set; } = "";
    /// <summary>PBKDF2 hash, "iterations.salt.hash" (base64). Never the plain password.</summary>
    public string PasswordHash { get; set; } = "";
}

public class GatewayConfig
{
    public ServerSettings Server { get; set; } = new();
    public ObservableCollection<ChannelConfig> Channels { get; set; } = new();
}

/// <summary>Live state of one connection.</summary>
public partial class ConnectionRuntime : ObservableObject
{
    public ConnectionRuntime(ConnectionConfig config) => Config = config;
    public ConnectionConfig Config { get; }
    public string Name => Config.Name;
    [ObservableProperty] private ConnectionState _state = ConnectionState.Stopped;
    [ObservableProperty] private string _message = "";
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
