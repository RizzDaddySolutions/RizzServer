using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RizzServer.Core;
using RizzServer.Drivers;
using RizzServer.Server;

namespace RizzServer.App;

/// <summary>Composition root and shared state for all pages.</summary>
public sealed partial class AppState : ObservableObject
{
    public static AppState Instance { get; } = new();

    private ILoggerFactory _loggers = null!;
    private ILogger _log = null!;

    public LogService Logs { get; } = new();
    public GatewayConfig Config { get; private set; } = new();
    public GatewayEngine Engine { get; private set; } = null!;
    public OpcUaHost Host { get; private set; } = null!;

    /// <summary>Connections and tags of the running engine, rebuilt on every start/stop (UI thread).</summary>
    public ObservableCollection<ConnectionRuntime> RunningConnections { get; } = new();
    public ObservableCollection<TagRuntime> RunningTags { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string _endpointUrl = "";

    public bool IsEditable => !IsRunning;

    public void Initialize()
    {
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(Logs));
        _log = _loggers.CreateLogger("RizzServer");

        Config = ConfigStore.Load();
        Engine = new GatewayEngine(Config, new DriverFactory(_loggers), _loggers.CreateLogger("Engine"));
        Host = new OpcUaHost(Engine, _loggers.CreateLogger("OPC UA"));
        Engine.RuntimeRebuilt += () => Application.Current.Dispatcher.Invoke(SyncRuntime);
        _log.LogInformation("Loaded {Count} connection(s) from {Path}", Config.Connections.Count, ConfigStore.ConfigPath);
    }

    private void SyncRuntime()
    {
        RunningConnections.Clear();
        foreach (var c in Engine.Connections) RunningConnections.Add(c);
        RunningTags.Clear();
        foreach (var t in Engine.Tags) RunningTags.Add(t);
    }

    public void Save()
    {
        try { ConfigStore.Save(Config); _log.LogInformation("Configuration saved"); }
        catch (Exception ex) { _log.LogError(ex, "Could not save configuration"); }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        Status = "Starting…";
        try
        {
            Save();
            await Engine.StartAsync();
            await Host.StartAsync();
            EndpointUrl = Host.EndpointUrl;
            IsRunning = true;
            Status = "Running";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start failed");
            await SafeStopAsync();
            Status = "Start failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanStart() => !IsRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        Status = "Stopping…";
        try { await SafeStopAsync(); Status = "Stopped"; }
        finally { IsBusy = false; }
    }

    private bool CanStop() => IsRunning && !IsBusy;

    private async Task SafeStopAsync()
    {
        try { await Host.StopAsync(); } catch (Exception ex) { _log.LogWarning(ex, "OPC UA stop"); }
        try { await Engine.StopAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Engine stop"); }
        IsRunning = false;
        EndpointUrl = "";
    }

    public void Shutdown()
    {
        Save();
        try { SafeStopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { /* exiting */ }
    }
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message);

public sealed class LogService : ILoggerProvider
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new UiLogger(this, categoryName);

    public void Dispose() { }

    private void Add(LogEntry entry)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.BeginInvoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > 1000) Entries.RemoveAt(0);
        });
    }

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
