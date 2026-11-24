using System.Text.Json;
using RizzServer.Core;

namespace RizzServer.Service.Api;

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

    public static ServerBody From(ServerSettings s) => new()
    {
        ApplicationName = s.ApplicationName,
        Port = s.Port,
        AllowNoSecurity = s.AllowNoSecurity,
        AutoAcceptCertificates = s.AutoAcceptCertificates,
        NamespaceUri = s.NamespaceUri,
        AllowAnonymous = s.AllowAnonymous,
        Users = s.Users.Select(u => u.Username).ToList(),
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
}

public sealed record UserBody(string Username, string Password);

public sealed record WriteBody(JsonElement Value);
