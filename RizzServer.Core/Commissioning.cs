using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace RizzServer.Core;

/// <summary>Counters a driver can report about its own polling (implemented by the driver base class).</summary>
public interface IDriverDiagnostics
{
    long Polls { get; }
    long PollErrors { get; }
    double LastPollMs { get; }
    double AveragePollMs { get; }
}

public sealed record DeviceEvent(DateTimeOffset Time, string Level, string Text);

/// <summary>A snapshot of how one device is doing: the first thing to look at when a value is wrong.</summary>
public sealed record DeviceDiagnostics(
    string Channel, string Device, string State, string Message,
    DateTimeOffset? ConnectedSince, int Reconnects, long Polls, long PollErrors, double LastPollMs, double AveragePollMs,
    long Updates, long BadUpdates, double UpdatesPerSecond, DateTimeOffset? LastUpdate, int Tags, int GoodTags, int BadTags);

/// <summary>Per-device runtime statistics and event history, kept by the engine.</summary>
public sealed class DeviceStats
{
    private readonly ConcurrentQueue<DeviceEvent> _events = new();
    private long _updates, _badUpdates;
    private int _reconnects;
    private DateTime _rateAt = DateTime.UtcNow;
    private long _rateCount;
    private double _rate;
    private bool _everConnected;

    public DateTimeOffset? ConnectedSince { get; private set; }
    public DateTimeOffset? LastUpdate { get; private set; }
    public int Reconnects => _reconnects;
    public long Updates => Interlocked.Read(ref _updates);
    public long BadUpdates => Interlocked.Read(ref _badUpdates);
    public IReadOnlyList<DeviceEvent> Events => [.. _events];

    public void Add(string level, string text)
    {
        _events.Enqueue(new DeviceEvent(DateTimeOffset.UtcNow, level, text));
        while (_events.Count > 200 && _events.TryDequeue(out _)) { }
    }

    public void OnState(ConnectionState state, string? message)
    {
        switch (state)
        {
            case ConnectionState.Connected:
                if (_everConnected) Interlocked.Increment(ref _reconnects);
                _everConnected = true;
                ConnectedSince = DateTimeOffset.UtcNow;
                Add("Info", "Connected");
                break;
            case ConnectionState.Faulted:
                ConnectedSince = null;
                Add("Error", string.IsNullOrEmpty(message) ? "Faulted" : message);
                break;
            case ConnectionState.Connecting: Add("Info", "Connecting"); break;
            case ConnectionState.Stopped: ConnectedSince = null; Add("Info", "Stopped"); break;
        }
    }

    public void OnUpdate(TagQuality quality)
    {
        Interlocked.Increment(ref _updates);
        if (quality != TagQuality.Good) Interlocked.Increment(ref _badUpdates);
        LastUpdate = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates per second since the previous call (smoothed over at least a second).</summary>
    public double Rate()
    {
        var now = DateTime.UtcNow;
        var dt = (now - _rateAt).TotalSeconds;
        if (dt >= 1)
        {
            var count = Updates;
            _rate = (count - _rateCount) / dt;
            _rateAt = now;
            _rateCount = count;
        }
        return _rate;
    }
}

public sealed record TagProbe(string Name, string Address, string Quality, object? Value, string? Error);

public sealed record DeviceTest(bool Connected, string Message, long ElapsedMs, IReadOnlyList<TagProbe> Tags);

/// <summary>
/// Try a device before (or without) adding it to the project: connect with its settings, read its tags (or a handful of
/// addresses) once, report what came back and why not. Runs its own driver instance, separate from the running runtime.
/// </summary>
public static class Commissioning
{
    public static async Task<DeviceTest> TestAsync(IDriverFactory factory, ChannelConfig channel, DeviceConfig device,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var clone = ConfigStore.Clone(device);
        clone.Enabled = true;
        var probe = new ChannelConfig { Name = channel.Name, DriverType = channel.DriverType, PluginId = channel.PluginId };
        foreach (var (k, v) in channel.Settings) probe.Settings[k] = v;
        probe.Devices.Add(clone);
        var config = probe.Flatten(clone);
        config.PollIntervalMs = Math.Max(50, Math.Min(config.PollIntervalMs, 500));   // answer quickly

        var sw = Stopwatch.StartNew();
        var results = new Dictionary<TagConfig, (object? Value, TagQuality Quality)>();
        var state = ConnectionState.Stopped;
        string? stateMessage = null;
        var done = new TaskCompletionSource();

        IDriver driver;
        try { driver = factory.Create(config); }
        catch (Exception ex) { return new DeviceTest(false, ex.Message, sw.ElapsedMilliseconds, []); }

        void Check()
        {
            if (state == ConnectionState.Connected && config.Tags.All(t => results.ContainsKey(t))) done.TrySetResult();
        }
        driver.TagUpdated += (tag, value, quality) => { lock (results) { results[tag] = (value, quality); Check(); } };
        driver.StateChanged += (s, m) => { lock (results) { state = s; if (m is not null) stateMessage = m; Check(); } };

        try
        {
            await driver.StartAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await done.Task.WaitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout: report what we have */ }
        }
        finally
        {
            try { await driver.StopAsync(); await driver.DisposeAsync(); } catch { /* best effort */ }
        }

        List<TagProbe> tags;
        bool connected;
        string message;
        lock (results)
        {
            connected = state == ConnectionState.Connected || results.Values.Any(r => r.Quality == TagQuality.Good);
            tags = config.Tags.Select(t =>
            {
                if (!results.TryGetValue(t, out var r)) return new TagProbe(t.Name, t.Address, "NoReply", null, connected ? "no value arrived in time" : stateMessage);
                var scaled = r.Value is null ? null : TagScaling.ToEngineering(t, r.Value);
                return new TagProbe(t.Name, t.Address, r.Quality.ToString(), Format(scaled),
                    r.Quality == TagQuality.Good ? null : "the device did not return a valid value for this address");
            }).ToList();
            message = connected
                ? (tags.Count == 0 ? "Connected." : $"Connected. {tags.Count(t => t.Quality == "Good")} of {tags.Count} value(s) read.")
                : (stateMessage ?? "Could not connect before the time ran out.");
        }
        return new DeviceTest(connected, message, sw.ElapsedMilliseconds, tags);
    }

    /// <summary>Reads a few addresses on a device's connection without touching its configured tags.</summary>
    public static Task<DeviceTest> ReadAsync(IDriverFactory factory, ChannelConfig channel, DeviceConfig device,
        IEnumerable<(string Address, TagDataType Type)> addresses, TimeSpan timeout, CancellationToken ct = default)
    {
        var scratch = ConfigStore.Clone(device);
        scratch.Tags.Clear();
        var i = 0;
        foreach (var (address, type) in addresses)
            scratch.Tags.Add(new TagConfig { Name = $"Probe{++i}", Address = address, DataType = type });
        return TestAsync(factory, channel, scratch, timeout, ct);
    }

    private static object? Format(object? v) => v switch
    {
        double d => Math.Round(d, 6),
        float f => Math.Round(f, 6),
        _ => v,
    };
}
