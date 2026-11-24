using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RizzServer.App.Backends;
using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;

namespace RizzServer.App;

/// <summary>Composition root and shared state for all views.</summary>
public sealed partial class AppState : ObservableObject
{
    public static AppState Instance { get; } = new();

    private ILoggerFactory _loggers = null!;
    private ILogger _log = null!;
    private LocalBackend _local = null!;
    private DispatcherTimer? _dirtyTimer;

    public LogService Logs { get; } = new();

    /// <summary>Where the project lives: this machine, or a headless service.</summary>
    public IGatewayBackend Backend { get; private set; } = null!;
    public GatewayConfig Config => Backend.Config;
    public RemoteBackend? Remote => Backend as RemoteBackend;
    public bool IsRemote => Backend.IsRemote;

    /// <summary>Connections and tags of the running engine, rebuilt on every start/stop (UI thread).</summary>
    public ObservableCollection<ConnectionRuntime> RunningConnections { get; } = new();
    public ObservableCollection<TagRuntime> RunningTags { get; } = new();

    /// <summary>Raised on the UI thread after the running collections were rebuilt.</summary>
    public event Action? RuntimeSynced;

    /// <summary>The project object was swapped (connected to / disconnected from a service); the tree must be rebuilt.</summary>
    public event Action? ConfigReplaced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(RuntimeText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string _endpointUrl = "";

    /// <summary>Remote only: the local copy has changes that are not on the service yet.</summary>
    [ObservableProperty] private bool _isDirty;

    public bool IsEditable => !IsRunning;
    public string RuntimeText => IsRunning ? "Runtime: Running" : "Runtime: Stopped";

    public string ConnectionText => !IsRemote ? "Local project"
        : Remote is { IsConnected: false } ? $"Remote: {Backend.Description} (not reachable)"
        : $"Remote: {Backend.Description}";

    public string WindowTitle => IsRemote ? $"RizzServer — {Backend.Description} (remote)" : "RizzServer — OPC UA Gateway";

    public void Initialize()
    {
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(Logs));
        _log = _loggers.CreateLogger("RizzServer");

        var runtime = new GatewayRuntime(ConfigStore.Load(), _loggers);
        _local = new LocalBackend(runtime);
        Attach(_local);
        _log.LogInformation("Loaded {Channels} channel(s), {Devices} device(s) from {Path}",
            Config.Channels.Count, Config.Channels.Sum(c => c.Devices.Count), ConfigStore.ConfigPath);

        _dirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _dirtyTimer.Tick += (_, _) => IsDirty = Remote?.IsDirty ?? false;
        _dirtyTimer.Start();
    }

    // ---- backend plumbing ----

    private void Attach(IGatewayBackend backend)
    {
        Backend = backend;
        backend.RuntimeRebuilt += OnRuntimeRebuilt;
        backend.StateChanged += OnBackendStateChanged;
        if (backend is RemoteBackend remote) remote.ConnectionChanged += OnConnectionChanged;
        SyncRuntime();
        IsRunning = backend.IsRunning;
        EndpointUrl = backend.EndpointUrl;
        Status = backend.IsRunning ? "Running" : "Stopped";
        RefreshConnectionInfo();
    }

    private void Detach(IGatewayBackend backend)
    {
        backend.RuntimeRebuilt -= OnRuntimeRebuilt;
        backend.StateChanged -= OnBackendStateChanged;
        if (backend is RemoteBackend remote) remote.ConnectionChanged -= OnConnectionChanged;
    }

    private void OnRuntimeRebuilt() => Dispatcher.UIThread.Post(SyncRuntime);

    private void OnBackendStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (IsBusy) return;   // a Start/Stop in progress reports its own result
        IsRunning = Backend.IsRunning;
        EndpointUrl = Backend.EndpointUrl;
        Status = IsRunning ? "Running" : "Stopped";
    });

    private void OnConnectionChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshConnectionInfo();
        if (Remote is { IsConnected: false } r) _log.LogWarning("Lost the connection to {Service}: {Error}", Backend.Description, r.ConnectionError);
        else if (IsRemote) _log.LogInformation("Reconnected to {Service}", Backend.Description);
    });

    private void RefreshConnectionInfo()
    {
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void SyncRuntime()
    {
        RunningConnections.Clear();
        foreach (var c in Backend.Connections) RunningConnections.Add(c);
        RunningTags.Clear();
        foreach (var t in Backend.Tags) RunningTags.Add(t);
        RuntimeSynced?.Invoke();
    }

    /// <summary>Opens the project of a headless service. Throws <see cref="ServiceApiException"/> when it can't be reached.</summary>
    public async Task ConnectRemoteAsync(Uri url, string apiKey)
    {
        if (IsRemote) throw new InvalidOperationException("Already connected to a service.");
        if (_local.IsRunning) throw new InvalidOperationException("Stop the local runtime before connecting to a service.");

        var remote = await RemoteBackend.ConnectAsync(url, apiKey, AddRemoteLog);
        Detach(_local);
        Attach(remote);
        ConfigReplaced?.Invoke();
        _log.LogInformation("Connected to {Service}: {Channels} channel(s), service is {State}",
            remote.Description, remote.Config.Channels.Count, remote.IsRunning ? "running" : "stopped");
    }

    /// <summary>Goes back to the local project. The remote service keeps running.</summary>
    public void DisconnectRemote()
    {
        if (Backend is not RemoteBackend remote) return;
        Detach(remote);
        remote.Dispose();
        Attach(_local);
        IsDirty = false;
        ConfigReplaced?.Invoke();
        _log.LogInformation("Disconnected from {Service}", remote.Description);
    }

    private void AddRemoteLog(LogLine line)
    {
        var level = Enum.TryParse<LogLevel>(line.Level, out var l) ? l : LogLevel.Information;
        var source = line.Source.Length > 18 ? line.Source[^18..] : line.Source;
        Dispatcher.UIThread.Post(() => Logs.Append(new LogEntry(line.Time.LocalDateTime, level, "Service/" + source, line.Message)));
    }

    // ---- saving ----

    public void LogInfo(string message) => _log.LogInformation("{Message}", message);
    public void LogWarning(string message) => _log.LogWarning("{Message}", message);

    /// <summary>
    /// Fire-and-forget save of a local project (used after small edits). A remote project is only
    /// pushed by an explicit <see cref="SaveAsync"/>, so a half-finished edit never reaches the service.
    /// </summary>
    public void Save()
    {
        if (IsRemote) return;
        try { ConfigStore.Save(Config); _log.LogInformation("Project saved to {Path}", ConfigStore.ConfigPath); }
        catch (Exception ex) { _log.LogError(ex, "Could not save project"); }
    }

    /// <summary>Saves locally, or pushes the project to the service. Returns false (and logs) on failure.</summary>
    public async Task<bool> SaveAsync()
    {
        var error = await Backend.SaveAsync();
        if (error is null)
        {
            _log.LogInformation(IsRemote ? "Project saved to {Where}" : "Project saved to {Where}", IsRemote ? Backend.Description : ConfigStore.ConfigPath);
            if (IsRemote) IsDirty = false;
            return true;
        }
        _log.LogError("Could not save the project: {Error}", error);
        Status = "Save failed: " + error.Split('\n')[0];
        LastSaveError = error;
        return false;
    }

    /// <summary>The full text of the last failed save (the status bar shows only its first line).</summary>
    public string LastSaveError { get; private set; } = "";

    // ---- runtime ----

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        Status = "Starting…";
        try
        {
            if (!await SaveAsync()) return;
            await Backend.StartAsync();
            EndpointUrl = Backend.EndpointUrl;
            IsRunning = Backend.IsRunning;
            Status = "Running";
        }
        catch (Exception ex)
        {
            IsRunning = Backend.IsRunning;
            EndpointUrl = Backend.EndpointUrl;
            var message = ex is ServiceApiException api ? api.FullMessage : ex.Message;
            _log.LogError("Start failed: {Error}", message);
            Status = "Start failed: " + message.Split('\n')[0];
        }
        finally { IsBusy = false; }
    }

    private bool CanStart() => !IsRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        Status = "Stopping…";
        try
        {
            await Backend.StopAsync();
            Status = "Stopped";
        }
        catch (Exception ex)
        {
            var message = ex is ServiceApiException api ? api.FullMessage : ex.Message;
            _log.LogError("Stop failed: {Error}", message);
            Status = "Stop failed: " + message.Split('\n')[0];
        }
        finally
        {
            IsRunning = Backend.IsRunning;
            EndpointUrl = Backend.EndpointUrl;
            IsBusy = false;
        }
    }

    private bool CanStop() => IsRunning && !IsBusy;

    public Task WriteAsync(TagRuntime tag, object value, CancellationToken ct = default) => Backend.WriteAsync(tag, value, ct);

    /// <summary>Called when the app exits. A remote service keeps running; a local runtime is stopped.</summary>
    public void Shutdown()
    {
        if (Backend is RemoteBackend remote)
        {
            remote.Dispose();
            return;
        }

        Save();
        try { _local.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { /* exiting */ }
    }
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public string Date => Time.ToString("yyyy-MM-dd");
    public string Clock => Time.ToString("HH:mm:ss.fff");
    public string Glyph => Level switch { LogLevel.Error or LogLevel.Critical => "✖", LogLevel.Warning => "▲", _ => "●" };
    public string Color => Level switch { LogLevel.Error or LogLevel.Critical => "#C0392B", LogLevel.Warning => "#B7791F", _ => "#2F6DB5" };
}

public sealed class LogService : ILoggerProvider
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new UiLogger(this, categoryName);

    public void Dispose() { }

    /// <summary>Adds an entry; call on the UI thread.</summary>
    public void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > 1000) Entries.RemoveAt(0);
    }

    private void Add(LogEntry entry) => Dispatcher.UIThread.Post(() => Append(entry));

    private sealed class UiLogger(LogService owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " — " + exception.Message;
            owner.Add(new LogEntry(DateTime.Now, logLevel, category, msg));
        }
    }
}
