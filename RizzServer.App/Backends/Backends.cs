using RizzServer.Core;
using RizzServer.Server;

namespace RizzServer.App.Backends;

/// <summary>
/// Where the project lives and runs: in this process (<see cref="LocalBackend"/>) or on a headless
/// service reached over REST (<see cref="RemoteBackend"/>). The UI only talks to this interface.
/// </summary>
public interface IGatewayBackend : IDisposable
{
    bool IsRemote { get; }

    /// <summary>"Local project" or the service address, for the status bar and Project page.</summary>
    string Description { get; }

    /// <summary>The project being edited (for a remote service: a local copy, pushed back on Save).</summary>
    GatewayConfig Config { get; }

    bool IsRunning { get; }
    string EndpointUrl { get; }
    IReadOnlyList<ConnectionRuntime> Connections { get; }
    IReadOnlyList<TagRuntime> Tags { get; }

    /// <summary>The connection/tag lists were replaced (start, stop). May fire on any thread.</summary>
    event Action? RuntimeRebuilt;

    /// <summary>The running state changed without the UI asking (a remote service was started elsewhere). May fire on any thread.</summary>
    event Action? StateChanged;

    Task StartAsync();
    Task StopAsync();

    /// <returns>null when saved, otherwise a message to show the user.</returns>
    Task<string?> SaveAsync();

    Task WriteAsync(TagRuntime tag, object value, CancellationToken ct = default);
}

/// <summary>The project file on this machine and the in-process runtime.</summary>
public sealed class LocalBackend(GatewayRuntime runtime) : IGatewayBackend
{
    public GatewayRuntime Runtime { get; } = runtime;

    public bool IsRemote => false;
    public string Description => "Local project";
    public GatewayConfig Config => Runtime.Config;
    public bool IsRunning => Runtime.IsRunning;
    public string EndpointUrl => Runtime.EndpointUrl;
    public IReadOnlyList<ConnectionRuntime> Connections => Runtime.Engine.Connections;
    public IReadOnlyList<TagRuntime> Tags => Runtime.Engine.Tags;

    public event Action? RuntimeRebuilt
    {
        add => Runtime.Engine.RuntimeRebuilt += value;
        remove => Runtime.Engine.RuntimeRebuilt -= value;
    }

    public event Action? StateChanged
    {
        add => Runtime.StateChanged += value;
        remove => Runtime.StateChanged -= value;
    }

    public Task StartAsync() => Runtime.StartAsync();
    public Task StopAsync() => Runtime.StopAsync();

    public Task<string?> SaveAsync()
    {
        try
        {
            Runtime.Save();
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) { return Task.FromResult<string?>(ex.Message); }
    }

    public Task WriteAsync(TagRuntime tag, object value, CancellationToken ct = default) => Runtime.Engine.WriteAsync(tag, value, ct);

    public void Dispose() { }
}
