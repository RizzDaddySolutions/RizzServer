using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using RizzServer.Core;
using RizzServer.Core.Api;

namespace RizzServer.App.Backends;

/// <summary>
/// A project that lives on a headless service. The UI edits a local copy exactly as for a local project;
/// <see cref="SaveAsync"/> pushes it to the service. Runtime state, device status, live values and the
/// event log are mirrored from the service (polling plus a Server-Sent Events stream for values).
/// </summary>
public sealed class RemoteBackend : IGatewayBackend
{
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ServiceClient _client;
    private readonly Action<LogLine> _onLog;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TagBody> _pending = new();
    private readonly DispatcherTimer _applyTimer;

    private Dictionary<string, TagRuntime> _byPath = new();
    private List<ConnectionRuntime> _connections = [];
    private List<TagRuntime> _tags = [];
    private CancellationTokenSource? _streamCts;
    private string _snapshot = "";
    private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
    private bool _firstLog = true;
    private int _tick;

    private RemoteBackend(ServiceClient client, Action<LogLine> onLog)
    {
        _client = client;
        _onLog = onLog;
        Config = new GatewayConfig();

        // Values arrive on a background thread; apply them on the UI thread in small batches.
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _applyTimer.Tick += (_, _) => ApplyPending();
        _applyTimer.Start();
    }

    /// <summary>Connects, reads the project and starts mirroring. Throws <see cref="ServiceApiException"/> on failure.</summary>
    public static async Task<RemoteBackend> ConnectAsync(Uri url, string apiKey, Action<LogLine> onLog, CancellationToken ct = default)
    {
        var client = new ServiceClient(url, apiKey);
        try
        {
            var status = await client.GetStatusAsync(ct);          // proves reachability and the key
            var project = await client.GetProjectAsync(ct);
            var backend = new RemoteBackend(client, onLog);
            backend.Load(project);
            await backend.ApplyStatusAsync(status, ct);
            _ = Task.Run(() => backend.PollAsync(backend._cts.Token));
            return backend;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public bool IsRemote => true;
    public string Description => _client.BaseUri.ToString().TrimEnd('/');
    public GatewayConfig Config { get; }
    public bool IsRunning { get; private set; }
    public string EndpointUrl { get; private set; } = "";
    public string? ServiceError { get; private set; }
    public IReadOnlyList<ConnectionRuntime> Connections => _connections;
    public IReadOnlyList<TagRuntime> Tags => _tags;

    public bool IsConnected { get; private set; } = true;
    public string ConnectionError { get; private set; } = "";

    public event Action? RuntimeRebuilt;
    public event Action? StateChanged;
    public event Action? ConnectionChanged;

    // ---- project mirror ----

    /// <summary>True when the local copy differs from what the service has.</summary>
    public bool IsDirty => Snapshot() != _snapshot;

    private string Snapshot()
    {
        var server = ServerBody.From(Config.Server);
        server.Users = [];   // users are managed by their own calls, never through the project
        return JsonSerializer.Serialize(new ProjectBody { Server = server, Channels = Config.Channels.ToList() }, SnapshotOptions);
    }

    /// <summary>Replaces the local copy in place, so the tree (which listens to these collections) follows.</summary>
    private void Load(ProjectBody project)
    {
        project.Server.ApplyTo(Config.Server);
        ReplaceUsers(project.Server.Users);
        Config.Channels.Clear();
        foreach (var c in project.Channels) Config.Channels.Add(c);
        _snapshot = Snapshot();
    }

    private void ReplaceUsers(IEnumerable<string> names)
    {
        Config.Server.Users.Clear();
        // Only names come back; the placeholder keeps the user list renderable.
        foreach (var n in names) Config.Server.Users.Add(new UserAccount { Username = n, PasswordHash = "(on the service)" });
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var project = await _client.GetProjectAsync(ct);
        Dispatcher.UIThread.Invoke(() => Load(project));
    }

    public async Task<string?> SaveAsync()
    {
        try
        {
            var server = ServerBody.From(Config.Server);
            await _client.PutProjectAsync(new ProjectBody { Server = server, Channels = Config.Channels.ToList() });
            _snapshot = Snapshot();
            return null;
        }
        catch (ServiceApiException ex) { return ex.IsConflict ? "The service is running, so its configuration is locked. Stop it first." : ex.FullMessage; }
    }

    public async Task AddUserAsync(string username, string password)
    {
        var names = await _client.AddUserAsync(username, password);
        Dispatcher.UIThread.Invoke(() => ReplaceUsers(names));
    }

    public async Task RemoveUserAsync(string username)
    {
        var names = await _client.RemoveUserAsync(username);
        Dispatcher.UIThread.Invoke(() => ReplaceUsers(names));
    }

    // ---- runtime ----

    public async Task StartAsync() => await ApplyStatusAsync(await _client.StartAsync());
    public async Task StopAsync() => await ApplyStatusAsync(await _client.StopAsync());

    public Task WriteAsync(TagRuntime tag, object value, CancellationToken ct = default) =>
        _client.WriteTagAsync(tag.NodePath, value, ct);

    private async Task ApplyStatusAsync(StatusBody status, CancellationToken ct = default)
    {
        ServiceError = status.LastError;
        EndpointUrl = status.Endpoint;
        if (status.Running == IsRunning) return;

        IsRunning = status.Running;
        if (IsRunning)
        {
            await BuildRuntimeAsync(ct);
            StartStream();
        }
        else
        {
            StopStream();
            _pending.Clear();   // late events must not bring a stopped tag back to Good
            foreach (var t in _tags) t.Quality = TagQuality.Bad;
            foreach (var c in _connections) { c.State = ConnectionState.Stopped; c.Message = ""; }
            RuntimeRebuilt?.Invoke();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Builds the live lists from the service's own view, which is authoritative while it runs.</summary>
    private async Task BuildRuntimeAsync(CancellationToken ct)
    {
        var tags = await _client.GetTagsAsync(ct);
        var devices = await _client.GetDeviceStatusAsync(ct);

        var byDevice = new Dictionary<(string, string), ConnectionConfig>();
        var connections = new List<ConnectionRuntime>();
        foreach (var d in devices)
        {
            var config = new ConnectionConfig { Name = $"{d.Channel}.{d.Device}", ChannelName = d.Channel, DeviceName = d.Device };
            byDevice[(d.Channel, d.Device)] = config;
            connections.Add(new ConnectionRuntime(config) { State = ParseState(d.State), Message = d.Message });
        }

        var runtimeTags = new List<TagRuntime>();
        var byPath = new Dictionary<string, TagRuntime>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            if (!byDevice.TryGetValue((t.Channel, t.Device), out var conn))
                byDevice[(t.Channel, t.Device)] = conn = new ConnectionConfig { Name = $"{t.Channel}.{t.Device}", ChannelName = t.Channel, DeviceName = t.Device };
            var cfg = new TagConfig
            {
                Name = t.Name,
                DataType = Enum.TryParse<TagDataType>(t.DataType, out var dt) ? dt : TagDataType.String,
                Access = Enum.TryParse<TagAccess>(t.Access, out var ac) ? ac : TagAccess.ReadOnly,
            };
            var rt = new TagRuntime(conn, cfg);
            Apply(rt, t);
            runtimeTags.Add(rt);
            byPath[t.Path] = rt;
        }

        _connections = connections;
        _tags = runtimeTags;
        _byPath = byPath;
        RuntimeRebuilt?.Invoke();
    }

    private static ConnectionState ParseState(string s) => Enum.TryParse<ConnectionState>(s, out var v) ? v : ConnectionState.Stopped;

    // ---- live values ----

    private void StartStream()
    {
        StopStream();
        var cts = _streamCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var t in _client.StreamTagsAsync(snapshot: true, cts.Token)) _pending[t.Path] = t;
                }
                catch (OperationCanceledException) { return; }
                catch (ServiceApiException) { /* unreachable or restarting: retry below */ }

                try { await Task.Delay(2000, cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts = null;
    }

    private void ApplyPending()
    {
        if (!IsRunning) { _pending.Clear(); return; }
        if (_pending.IsEmpty) return;
        foreach (var path in _pending.Keys.ToArray())
            if (_pending.TryRemove(path, out var body) && _byPath.TryGetValue(path, out var rt))
                Apply(rt, body);
    }

    private static void Apply(TagRuntime rt, TagBody body)
    {
        rt.Value = TagValueConverter.Coerce(FromJson(body.Value), rt.Config.DataType) ?? FromJson(body.Value);
        rt.Quality = Enum.TryParse<TagQuality>(body.Quality, out var q) ? q : TagQuality.Bad;
        rt.Timestamp = body.Timestamp;
    }

    /// <summary>System.Text.Json hands back object values as JsonElement.</summary>
    private static object? FromJson(object? value) => value is JsonElement e
        ? e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : value;

    // ---- background mirroring ----

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _client.GetStatusAsync(ct);
                SetConnected(true, "");
                await ApplyStatusAsync(status, ct);

                if (IsRunning) ApplyDeviceStatus(await _client.GetDeviceStatusAsync(ct));
                if (_tick++ % 2 == 0) await PullLogAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ServiceApiException ex) { SetConnected(false, ex.Message); }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyDeviceStatus(List<DeviceStatusBody> statuses)
    {
        foreach (var s in statuses)
            if (_connections.FirstOrDefault(c => c.Config.ChannelName == s.Channel && c.Config.DeviceName == s.Device) is { } c)
            {
                var state = ParseState(s.State);
                if (c.State != state) c.State = state;
                if (c.Message != s.Message) c.Message = s.Message;
            }
    }

    private async Task PullLogAsync(CancellationToken ct)
    {
        var lines = await _client.GetLogAsync(200, ct);
        var fresh = lines.Where(l => l.Time > _lastLog).OrderBy(l => l.Time).ToList();
        if (fresh.Count == 0) return;

        _lastLog = fresh[^1].Time;
        if (_firstLog) { fresh = fresh.TakeLast(30).ToList(); _firstLog = false; }   // don't flood the log on connect
        foreach (var l in fresh) _onLog(l);
    }

    private void SetConnected(bool connected, string error)
    {
        if (IsConnected == connected && ConnectionError == error) return;
        IsConnected = connected;
        ConnectionError = error;
        ConnectionChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _applyTimer.Stop();
        _client.Dispose();
    }
}
