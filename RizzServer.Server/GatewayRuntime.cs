using Microsoft.Extensions.Logging;
using RizzServer.Core;
using RizzServer.Drivers;

namespace RizzServer.Server;

/// <summary>
/// The whole gateway behind one object: project config, driver engine and OPC UA endpoint.
/// Shared by the desktop app and the headless service so both start, stop and fail the same way.
/// </summary>
public sealed class GatewayRuntime
{
    private readonly ILogger _log;

    public GatewayRuntime(GatewayConfig config, ILoggerFactory loggers)
    {
        Config = config;
        _log = loggers.CreateLogger("RizzServer");
        Engine = new GatewayEngine(config, new DriverFactory(loggers), loggers.CreateLogger("Engine"));
        Host = new OpcUaHost(Engine, loggers.CreateLogger("OPC UA"));
        Outputs = new NorthboundManager(Engine, loggers);
    }

    public GatewayConfig Config { get; }
    public GatewayEngine Engine { get; }
    public OpcUaHost Host { get; }
    public NorthboundManager Outputs { get; }

    public bool IsRunning { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string? LastError { get; private set; }
    public string EndpointUrl => IsRunning ? Host.EndpointUrl : "";

    /// <summary>Serialises start/stop and configuration edits (REST handlers take it around a change).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Raised after the running state changed.</summary>
    public event Action? StateChanged;

    /// <summary>True while this node is the standby of a redundant pair: OPC UA is up (low ServiceLevel) but the drivers are idle.</summary>
    public bool IsStandby { get; private set; }

    public const byte ServiceLevelActive = 250, ServiceLevelStandby = 100, ServiceLevelDegraded = 50;

    public Task StartAsync() => StartAsync(standby: false);

    public async Task StartAsync(bool standby)
    {
        await Gate.WaitAsync();
        try
        {
            if (IsRunning) return;
            try
            {
                LastError = null;
                await Engine.StartAsync(connectDrivers: !standby);
                Host.ServiceLevel = standby ? ServiceLevelStandby : ServiceLevelActive;
                await Host.StartAsync();
                if (!standby) await Outputs.StartAsync();
                IsStandby = standby;
                IsRunning = true;
                StartedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.LogError(ex, "Start failed");
                await StopCoreAsync();
                throw;
            }
        }
        finally { Gate.Release(); }
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        await Gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { Gate.Release(); }
        StateChanged?.Invoke();
    }

    /// <summary>Swaps in a new project; runs again afterwards if it was running.</summary>
    public async Task ReloadAsync(GatewayConfig candidate)
    {
        var wasRunning = IsRunning;
        var wasStandby = IsStandby;
        await StopAsync();
        await Gate.WaitAsync();
        try { ReplaceConfig(candidate); }
        finally { Gate.Release(); }
        if (wasRunning) await StartAsync(wasStandby);
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private async Task StopCoreAsync()
    {
        try { await Outputs.StopAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Outputs stop"); }
        try { await Host.StopAsync(); } catch (Exception ex) { _log.LogWarning(ex, "OPC UA stop"); }
        try { await Engine.StopAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Engine stop"); }
        IsRunning = false;
        IsStandby = false;
        StartedAt = null;
    }

    /// <summary>A standby node takes over: connect the drivers, start the outputs, raise the ServiceLevel.</summary>
    public async Task PromoteAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!IsRunning || !IsStandby) return;
            await Engine.ActivateAsync();
            await Outputs.StartAsync();
            Host.ServiceLevel = ServiceLevelActive;
            IsStandby = false;
        }
        finally { Gate.Release(); }
        StateChanged?.Invoke();
    }

    /// <summary>An active node hands over: drivers and outputs stop, the OPC UA server stays up with a low ServiceLevel.</summary>
    public async Task DemoteAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!IsRunning || IsStandby) return;
            Host.ServiceLevel = ServiceLevelStandby;   // clients move away first
            await Outputs.StopAsync();
            await Engine.DeactivateAsync();
            IsStandby = true;
        }
        finally { Gate.Release(); }
        StateChanged?.Invoke();
    }

    public void Save() => ConfigStore.Save(Config);

    /// <summary>Set when the project comes from a file under version control; the API then refuses edits (change the file instead).</summary>
    public string? SourceFile { get; set; }

    /// <summary>Copies a validated project into the live project object (the engine and the OPC UA server hold that object).</summary>
    public void ReplaceConfig(GatewayConfig candidate) => ConfigStore.CopyInto(Config, candidate);
}
