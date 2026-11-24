using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>What leaves RizzServer for a tag: the same JSON for MQTT and HTTP.</summary>
public static class OutputPayload
{
    public static Dictionary<string, object?> Of(TagRuntime t)
    {
        var d = new Dictionary<string, object?>
        {
            ["path"] = t.NodePath,
            ["value"] = t.Value,
            ["quality"] = t.Quality.ToString(),
            ["timestamp"] = t.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(t.Config.Units)) d["units"] = t.Config.Units;
        return d;
    }

    /// <summary>The identity of a value for "only when changed": value text plus quality.</summary>
    public static string Fingerprint(TagRuntime t) => $"{Convert.ToString(t.Value, CultureInfo.InvariantCulture)}|{t.Quality}";

    public static string Expand(string template, TagRuntime t) =>
        template.Replace("{channel}", t.ChannelName).Replace("{device}", t.DeviceName).Replace("{tag}", t.Config.Name)
                .Replace("{path}", t.NodePath.Replace('.', '/'));
}

/// <summary>Glob filter on tag paths: <c>Plant.*</c>, <c>*.Temp*</c>; several patterns separated by ';'.</summary>
public sealed class TagFilter
{
    private readonly Regex[] _patterns;

    public TagFilter(string spec) =>
        _patterns = (string.IsNullOrWhiteSpace(spec) ? "*" : spec)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

    public bool Matches(string path) => _patterns.Any(p => p.IsMatch(path));
}

public abstract class OutputBase : IAsyncDisposable
{
    private long _sent, _failed;
    private DateTimeOffset? _lastSent;

    protected OutputBase(NorthboundConfig config, GatewayEngine engine, ILogger log)
    {
        Config = config;
        Engine = engine;
        Log = log;
        Filter = new TagFilter(config.Setting("Filter", "*"));
    }

    public NorthboundConfig Config { get; }
    protected GatewayEngine Engine { get; }
    protected ILogger Log { get; }
    protected TagFilter Filter { get; }

    public string State { get; protected set; } = "Stopped";
    public string Message { get; protected set; } = "";

    public OutputStatus Status => new(Config.Name, Config.Kind.ToString(), State, Message,
        Interlocked.Read(ref _sent), Interlocked.Read(ref _failed), _lastSent);

    protected void Sent(int count) { Interlocked.Add(ref _sent, count); _lastSent = DateTimeOffset.UtcNow; }
    protected void Failed(int count) => Interlocked.Add(ref _failed, count);

    protected IEnumerable<TagRuntime> MatchingTags() => Engine.Tags.Where(t => Filter.Matches(t.NodePath));

    public abstract Task StartAsync();
    public abstract Task StopAsync();
    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>Publishes tag values to an MQTT broker: one topic per tag, retained, with an online/offline status topic.</summary>
public sealed class MqttOutput : OutputBase
{
    private readonly Channel<TagRuntime> _queue = Channel.CreateBounded<TagRuntime>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, string> _lastPublished = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IMqttClient? _client;

    public MqttOutput(NorthboundConfig config, GatewayEngine engine, ILogger log) : base(config, engine, log) { }

    private void OnTag(TagRuntime t) { if (Filter.Matches(t.NodePath)) _queue.Writer.TryWrite(t); }

    public override Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        Engine.TagUpdated += OnTag;
        State = "Connecting";
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_cts is null) return;
        Engine.TagUpdated -= OnTag;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose(); _cts = null;
        State = "Stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var statusTopic = Config.Setting("StatusTopic", "rizzserver/status");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(Config.Setting("Host", "localhost"), Config.SettingInt("Port", 1883))
                    .WithClientId(Config.Setting("ClientId", "rizzserver-out"))
                    .WithCleanSession()
                    .WithWillTopic(statusTopic).WithWillPayload("offline").WithWillRetain(true)
                    .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
                var user = Config.Setting("Username");
                if (user.Length > 0) builder.WithCredentials(user, SecretStore.Resolve(Config.Setting("Password")));
                if (Config.SettingBool("UseTls")) builder.WithTlsOptions(o => o.UseTls());

                _client = new MqttClientFactory().CreateMqttClient();
                await _client.ConnectAsync(builder.Build(), ct);
                await Send(statusTopic, "online", retain: true, ct);
                State = "Connected"; Message = "";
                _lastPublished.Clear();

                // A fresh connection gets the current state of everything, then changes.
                foreach (var t in MatchingTags()) await PublishTag(t, force: true, ct);

                var interval = Config.Setting("PublishMode", "OnChange") == "Interval"
                    ? TimeSpan.FromMilliseconds(Math.Max(100, Config.SettingInt("IntervalMs", 5000))) : (TimeSpan?)null;
                var nextSnapshot = DateTime.UtcNow + (interval ?? TimeSpan.Zero);

                while (!ct.IsCancellationRequested)
                {
                    if (!_client.IsConnected) throw new IOException("Connection to the broker was lost.");
                    if (interval is { } every)
                    {
                        _queue.Reader.TryRead(out _);   // in Interval mode the queue only wakes us; snapshots carry the data
                        if (DateTime.UtcNow >= nextSnapshot)
                        {
                            foreach (var t in MatchingTags()) await PublishTag(t, force: true, ct);
                            nextSnapshot = DateTime.UtcNow + every;
                        }
                        await Task.Delay(50, ct);
                        continue;
                    }

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(1000);   // wake up to notice a dead connection
                    try
                    {
                        var t = await _queue.Reader.ReadAsync(wait.Token);
                        await PublishTag(t, force: false, ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                State = "Faulted"; Message = ex.Message;
                Log.LogWarning("[{Output}] {Error}", Config.Name, ex.Message);
            }
            finally
            {
                var c = _client; _client = null;
                if (c is not null)
                {
                    try { if (c.IsConnected) await c.DisconnectAsync(); } catch { /* best effort */ }
                    c.Dispose();
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishTag(TagRuntime t, bool force, CancellationToken ct)
    {
        var fp = OutputPayload.Fingerprint(t);
        if (!force && _lastPublished.TryGetValue(t.NodePath, out var last) && last == fp) return;
        var topic = OutputPayload.Expand(Config.Setting("TopicTemplate", "rizzserver/{channel}/{device}/{tag}"), t);
        var body = Config.Setting("Format", "Json") == "Value"
            ? Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? ""
            : JsonSerializer.Serialize(OutputPayload.Of(t));
        await Send(topic, body, Config.SettingBool("Retain", true), ct);
        _lastPublished[t.NodePath] = fp;
    }

    private async Task Send(string topic, string payload, bool retain, CancellationToken ct)
    {
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(Encoding.UTF8.GetBytes(payload)).WithRetainFlag(retain)
                .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)Math.Clamp(Config.SettingInt("Qos", 1), 0, 2))
                .Build();
            await _client!.PublishAsync(message, ct);
            Sent(1);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { Failed(1); throw; }
    }
}

/// <summary>Posts batches of tag values as JSON to a URL (data lakes, historians, automation platforms, serverless endpoints).</summary>
public sealed class HttpOutput : OutputBase
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, object?>> _pending = new();
    private readonly Dictionary<string, string> _lastSent = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HttpClient? _http;

    public HttpOutput(NorthboundConfig config, GatewayEngine engine, ILogger log) : base(config, engine, log) { }

    private void OnTag(TagRuntime t)
    {
        if (!Filter.Matches(t.NodePath)) return;
        var fp = OutputPayload.Fingerprint(t);
        lock (_lock)
        {
            if (Config.SettingBool("OnlyChanged", true) && _lastSent.TryGetValue(t.NodePath, out var last) && last == fp && !_pending.ContainsKey(t.NodePath)) return;
            _pending[t.NodePath] = OutputPayload.Of(t);   // latest value per tag wins
            if (_pending.Count > 50000) _pending.Remove(_pending.Keys.First());
        }
    }

    public override Task StartAsync()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var auth = Config.Setting("AuthHeader");
        if (auth.Length > 0)
        {
            var value = SecretStore.Resolve(auth);
            var i = value.IndexOf(' ');
            _http.DefaultRequestHeaders.Authorization = i > 0 ? new AuthenticationHeaderValue(value[..i], value[(i + 1)..]) : new AuthenticationHeaderValue(value);
        }
        _cts = new CancellationTokenSource();
        Engine.TagUpdated += OnTag;
        State = "Running";
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_cts is null) return;
        Engine.TagUpdated -= OnTag;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose(); _cts = null;
        _http?.Dispose(); _http = null;
        State = "Stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, Config.SettingInt("IntervalMs", 5000)));
        var batchSize = Math.Max(1, Config.SettingInt("BatchSize", 500));
        var url = Config.Setting("Url");
        var onlyChanged = Config.SettingBool("OnlyChanged", true);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }

            List<Dictionary<string, object?>> items;
            lock (_lock)
            {
                if (!onlyChanged) foreach (var t in MatchingTags()) _pending[t.NodePath] = OutputPayload.Of(t);
                items = [.. _pending.Values];
                _pending.Clear();
            }
            if (items.Count == 0) continue;

            for (var i = 0; i < items.Count; i += batchSize)
            {
                var chunk = items.Skip(i).Take(batchSize).ToList();
                try
                {
                    var body = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), values = chunk });
                    using var response = await _http!.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
                    response.EnsureSuccessStatusCode();
                    lock (_lock) foreach (var v in chunk) _lastSent[(string)v["path"]!] = $"{Convert.ToString(v["value"], CultureInfo.InvariantCulture)}|{v["quality"]}";
                    Sent(chunk.Count);
                    State = "Running"; Message = "";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Keep the values for the next try unless a newer one arrived meanwhile.
                    lock (_lock) foreach (var v in items.Skip(i)) _pending.TryAdd((string)v["path"]!, v);
                    Failed(chunk.Count);
                    State = "Faulted"; Message = ex.Message;
                    Log.LogWarning("[{Output}] {Error}", Config.Name, ex.Message);
                    break;
                }
            }
        }
    }
}

/// <summary>Creates, starts and stops the outputs of a project.</summary>
public sealed class NorthboundManager
{
    private readonly GatewayEngine _engine;
    private readonly ILoggerFactory _loggers;
    private readonly List<OutputBase> _outputs = [];

    public NorthboundManager(GatewayEngine engine, ILoggerFactory loggers) { _engine = engine; _loggers = loggers; }

    public IReadOnlyList<OutputStatus> Statuses => [.. _outputs.Select(o => o.Status)];

    public async Task StartAsync()
    {
        await StopAsync();
        foreach (var cfg in _engine.Config.Northbound.Where(n => n.Enabled))
        {
            var log = _loggers.CreateLogger($"Output.{cfg.Name}");
            OutputBase output = cfg.Kind == NorthboundKind.Mqtt ? new MqttOutput(cfg, _engine, log) : new HttpOutput(cfg, _engine, log);
            try { await output.StartAsync(); _outputs.Add(output); }
            catch (Exception ex)
            {
                log.LogError("Output {Name} did not start: {Error}", cfg.Name, ex.Message);
                _outputs.Add(output);   // stays visible with its state
            }
        }
    }

    public async Task StopAsync()
    {
        foreach (var o in _outputs) { try { await o.StopAsync(); } catch { /* shutting down */ } }
        _outputs.Clear();
    }
}
