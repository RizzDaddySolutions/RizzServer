using System.Text.Json;

namespace RizzServer.Core.Api;

public sealed record ErrorBody(string Error, IReadOnlyList<string>? Details = null);

public sealed record StatusBody(
    bool Running, DateTimeOffset? StartedAt, string Endpoint, string? LastError,
    int Channels, int Devices, int Tags, int ConnectedDevices, int FaultedDevices);

public sealed record DeviceStatusBody(string Channel, string Device, string State, string Message);

public sealed record TagBody(
    string Path, string Channel, string Device, string Name, object? Value,
    string Quality, DateTime Timestamp, string DataType, string Access);

public sealed class ServerBody
{
    public string ApplicationName { get; set; } = "RizzServer";
    public int Port { get; set; } = 4840;
    public bool AllowNoSecurity { get; set; } = true;
    public bool AutoAcceptCertificates { get; set; } = true;
    public string NamespaceUri { get; set; } = "urn:rizzserver:data";
    public bool AllowAnonymous { get; set; } = true;

    /// <summary>Read-only: names only. Manage users with /api/v1/server/users.</summary>
    public List<string> Users { get; set; } = new();

    /// <summary>Read-only: name → role.</summary>
    public Dictionary<string, string> Roles { get; set; } = new();

    public static ServerBody From(ServerSettings s) => new()
    {
        ApplicationName = s.ApplicationName,
        Port = s.Port,
        AllowNoSecurity = s.AllowNoSecurity,
        AutoAcceptCertificates = s.AutoAcceptCertificates,
        NamespaceUri = s.NamespaceUri,
        AllowAnonymous = s.AllowAnonymous,
        Users = s.Users.Select(u => u.Username).ToList(),
        Roles = s.Users.ToDictionary(u => u.Username, u => u.Role.ToString()),
    };

    public void ApplyTo(ServerSettings s)
    {
        s.ApplicationName = ApplicationName;
        s.Port = Port;
        s.AllowNoSecurity = AllowNoSecurity;
        s.AutoAcceptCertificates = AutoAcceptCertificates;
        s.NamespaceUri = NamespaceUri;
        s.AllowAnonymous = AllowAnonymous;
    }
}

public sealed class ProjectBody
{
    public ServerBody Server { get; set; } = new();
    public List<ChannelConfig> Channels { get; set; } = new();

    /// <summary>Null = leave the stored value alone (older clients don't send these).</summary>
    public List<DeviceTemplate>? Templates { get; set; }
    public List<NorthboundConfig>? Northbound { get; set; }
    public RedundancySettings? Redundancy { get; set; }
}

public sealed record UserBody(string Username, string Password, UserRole? Role = null);

public sealed record WriteBody(JsonElement Value);

public sealed record LogLine(DateTimeOffset Time, string Level, string Source, string Message);
