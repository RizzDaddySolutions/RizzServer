using Microsoft.Extensions.Logging;

namespace RizzServer.Core;

/// <summary>Owns the drivers and the live tag table. The OPC UA layer mirrors <see cref="Tags"/>.</summary>
public sealed class GatewayEngine
{
    private readonly IDriverFactory _factory;
    private readonly ILogger _log;
    private readonly Dictionary<ConnectionRuntime, IDriver> _drivers = new();
    private readonly Dictionary<(ConnectionConfig, TagConfig), TagRuntime> _tagMap = new();

    public GatewayEngine(GatewayConfig config, IDriverFactory factory, ILogger log)
    {
        Config = config;
        _factory = factory;
        _log = log;
    }

    public GatewayConfig Config { get; }
    public IReadOnlyList<ConnectionRuntime> Connections { get; private set; } = [];
    public IReadOnlyList<TagRuntime> Tags { get; private set; } = [];
    public bool IsRunning { get; private set; }

    public event Action<TagRuntime>? TagUpdated;
    /// <summary>Raised after Connections/Tags were rebuilt (start or stop).</summary>
    public event Action? RuntimeRebuilt;

    public async Task StartAsync()
    {
        if (IsRunning) return;

        var conns = new List<ConnectionRuntime>();
        var tags = new List<TagRuntime>();
        _tagMap.Clear();

        foreach (var channel in Config.Channels.Where(c => c.Enabled))
        foreach (var device in channel.Devices.Where(d => d.Enabled))
        {
            var c = channel.Flatten(device);
            conns.Add(new ConnectionRuntime(c));
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

        foreach (var cr in conns)
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
        await driver.WriteAsync(tag.Config, coerced, ct);
        // Optimistic update; the next poll confirms.
        Apply(tag, coerced, TagQuality.Good);
    }

    private void OnTagUpdated(ConnectionConfig conn, TagConfig tag, object? value, TagQuality quality)
    {
        if (!_tagMap.TryGetValue((conn, tag), out var rt)) return;
        var coerced = value is null ? null : TagValueConverter.Coerce(value, tag.DataType);
        if (value is not null && coerced is null) quality = TagQuality.Bad;
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
