using Microsoft.Extensions.Logging;

namespace RizzServer.Core;

/// <summary>Owns the drivers and the live tag table. The OPC UA layer mirrors <see cref="Tags"/>.</summary>
public sealed class GatewayEngine
{
    private readonly IDriverFactory _factory;
    private readonly ILogger _log;
    private readonly Dictionary<ConnectionRuntime, IDriver> _drivers = new();
    private readonly Dictionary<(ConnectionConfig, TagConfig), TagRuntime> _tagMap = new();
    private readonly Dictionary<ConnectionConfig, DeviceStats> _stats = new();

    public GatewayEngine(GatewayConfig config, IDriverFactory factory, ILogger log)
    {
        Config = config;
        _factory = factory;
        _log = log;
    }

    public GatewayConfig Config { get; }
    public IDriverFactory Factory => _factory;
    public IReadOnlyList<ConnectionRuntime> Connections { get; private set; } = [];
    public IReadOnlyList<TagRuntime> Tags { get; private set; } = [];
    public bool IsRunning { get; private set; }

    /// <summary>Set by the host to record security-relevant events: (action, user, target, detail).</summary>
    public Action<string, string, string, string>? Audit { get; set; }

    /// <summary>OPC UA password logins: five wrong passwords for one user name lock it for five minutes.</summary>
    public FailureTracker LoginFailures { get; } = new(5);

    public event Action<TagRuntime>? TagUpdated;
    /// <summary>Raised after Connections/Tags were rebuilt (start or stop).</summary>
    public event Action? RuntimeRebuilt;

    /// <summary>Builds the tag table and (unless <paramref name="connectDrivers"/> is false) connects the drivers. A standby node builds the table only.</summary>
    public async Task StartAsync(bool connectDrivers = true)
    {
        if (IsRunning) return;

        var conns = new List<ConnectionRuntime>();
        var tags = new List<TagRuntime>();
        _tagMap.Clear();
        _stats.Clear();

        foreach (var channel in Config.Channels.Where(c => c.Enabled))
        foreach (var device in channel.Devices.Where(d => d.Enabled))
        {
            var c = channel.Flatten(device);
            var runtime = new ConnectionRuntime(c);
            conns.Add(runtime);
            _stats[c] = runtime.Stats;
            foreach (var t in c.Tags)
            {
                var rt = new TagRuntime(c, t);
                tags.Add(rt);
                _tagMap[(c, t)] = rt;
            }
        }

        Connections = conns;
        Tags = tags;
        IsRunning = true;
        RuntimeRebuilt?.Invoke();

        if (connectDrivers) await ActivateAsync();
    }

    public bool DriversActive => _drivers.Count > 0;

    /// <summary>Connects the drivers of an already built table (a standby node taking over).</summary>
    public async Task ActivateAsync()
    {
        if (!IsRunning || _drivers.Count > 0) return;
        foreach (var cr in Connections)
        {
            try
            {
                var driver = _factory.Create(cr.Config);
                var cfg = cr.Config;
                driver.TagUpdated += (tag, value, q) => OnTagUpdated(cfg, tag, value, q);
                driver.StateChanged += (state, msg) =>
                {
                    cr.State = state;
                    cr.Message = msg ?? "";
                    cr.Stats.OnState(state, msg);
                    if (string.IsNullOrEmpty(msg)) _log.LogInformation("[{Conn}] {State}", cfg.Name, state);
                    else _log.LogWarning("[{Conn}] {State}: {Msg}", cfg.Name, state, msg);
                };
                _drivers[cr] = driver;
                await driver.StartAsync();
            }
            catch (Exception ex)
            {
                cr.State = ConnectionState.Faulted;
                cr.Message = ex.Message;
                _log.LogError(ex, "Failed to start connection {Conn}", cr.Name);
            }
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        foreach (var (cr, driver) in _drivers)
        {
            try { await driver.StopAsync(); await driver.DisposeAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error stopping {Conn}", cr.Name); }
            cr.State = ConnectionState.Stopped;
        }
        _drivers.Clear();
        IsRunning = false;
        foreach (var t in Tags) t.Quality = TagQuality.Bad;
        RuntimeRebuilt?.Invoke();
    }

    /// <summary>Disconnects the drivers but keeps the table: tags go Bad, the OPC UA nodes stay (a node handing over to its peer).</summary>
    public async Task DeactivateAsync()
    {
        if (!IsRunning || _drivers.Count == 0) return;
        foreach (var (cr, driver) in _drivers)
        {
            try { await driver.StopAsync(); await driver.DisposeAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error stopping {Conn}", cr.Name); }
            cr.State = ConnectionState.Stopped;
        }
        _drivers.Clear();
        foreach (var t in Tags) Apply(t, t.Value, TagQuality.Bad);
    }

    public async Task WriteAsync(TagRuntime tag, object value, CancellationToken ct = default)
    {
        if (tag.Config.Access != TagAccess.ReadWrite)
            throw new InvalidOperationException($"Tag {tag.NodePath} is read-only.");

        var cr = Connections.FirstOrDefault(c => c.Config == tag.Connection)
                 ?? throw new InvalidOperationException("Connection not running.");
        if (!_drivers.TryGetValue(cr, out var driver))
            throw new InvalidOperationException("Driver not running.");

        var coerced = TagValueConverter.Coerce(value, tag.Config.DataType)
                      ?? throw new ArgumentException($"Cannot convert '{value}' to {tag.Config.DataType}.");
        if (!TagScaling.WithinRange(tag.Config, coerced))
            throw new ArgumentOutOfRangeException(nameof(value), $"{coerced} is outside the range {tag.Config.EuMin}..{tag.Config.EuMax} of {tag.NodePath}.");
        // The device holds the raw value; clients work in engineering units.
        await driver.WriteAsync(tag.Config, TagScaling.ToRaw(tag.Config, coerced), ct);
        // Optimistic update; the next poll confirms.
        Apply(tag, coerced, TagQuality.Good);
    }

    /// <summary>Per-device health for the commissioning views and the diagnostics API.</summary>
    public IReadOnlyList<DeviceDiagnostics> GetDiagnostics() => [.. Connections.Select(c =>
    {
        var d = _drivers.TryGetValue(c, out var driver) ? driver as IDriverDiagnostics : null;
        var tags = Tags.Where(t => t.Connection == c.Config).ToList();
        return new DeviceDiagnostics(c.Config.ChannelName, c.Config.DeviceName, c.State.ToString(), c.Message,
            c.Stats.ConnectedSince, c.Stats.Reconnects, d?.Polls ?? 0, d?.PollErrors ?? 0, d?.LastPollMs ?? 0, d?.AveragePollMs ?? 0,
            c.Stats.Updates, c.Stats.BadUpdates, Math.Round(c.Stats.Rate(), 1), c.Stats.LastUpdate,
            tags.Count, tags.Count(t => t.Quality == TagQuality.Good), tags.Count(t => t.Quality != TagQuality.Good));
    })];

    public IReadOnlyList<DeviceEvent> GetEvents(string channel, string device) =>
        Connections.FirstOrDefault(c => string.Equals(c.Config.ChannelName, channel, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(c.Config.DeviceName, device, StringComparison.OrdinalIgnoreCase))?.Stats.Events ?? [];

    private void OnTagUpdated(ConnectionConfig conn, TagConfig tag, object? value, TagQuality quality)
    {
        if (!_tagMap.TryGetValue((conn, tag), out var rt)) return;
        if (_stats.TryGetValue(conn, out var stats)) stats.OnUpdate(quality);
        var scaled = value is null ? null : TagScaling.ToEngineering(tag, value);
        var coerced = scaled is null ? null : TagValueConverter.Coerce(scaled, tag.DataType);
        if (value is not null && coerced is null) quality = TagQuality.Bad;
        if (quality == TagQuality.Good && coerced is not null && !TagScaling.WithinRange(tag, coerced)) quality = TagQuality.Uncertain;
        Apply(rt, coerced ?? rt.Value, quality);
    }

    private void Apply(TagRuntime rt, object? value, TagQuality quality)
    {
        rt.Value = value;
        rt.Quality = quality;
        rt.Timestamp = DateTime.UtcNow;
        TagUpdated?.Invoke(rt);
    }
}
