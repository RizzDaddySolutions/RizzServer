using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.Core;

public enum DriverType { ModbusTcp, ModbusRtu, Serial, Mqtt, OpcDa, Rs232Poll, BinaryFrame }

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

public partial class ConnectionConfig : ObservableObject
{
    [ObservableProperty] private string _name = "Connection1";
    [ObservableProperty] private DriverType _driverType = DriverType.ModbusTcp;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _pollIntervalMs = 1000;

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
    public ObservableCollection<ConnectionConfig> Connections { get; set; } = new();
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
    public string NodePath => $"{Connection.Name}.{Config.Name}";

    [ObservableProperty] private object? _value;
    [ObservableProperty] private TagQuality _quality = TagQuality.Bad;
    [ObservableProperty] private DateTime _timestamp = DateTime.UtcNow;
}
