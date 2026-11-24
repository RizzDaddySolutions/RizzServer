using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

/// <summary>One editable driver setting, written straight back to a channel's or device's settings dictionary.</summary>
public sealed partial class SettingItem : ObservableObject
{
    private readonly IDictionary<string, string> _target;

    public SettingItem(IDictionary<string, string> target, SettingDescriptor descriptor)
    {
        _target = target;
        Descriptor = descriptor;
        _value = target.TryGetValue(descriptor.Key, out var v) ? v : descriptor.Default;
        target[descriptor.Key] = _value;
    }

    public SettingDescriptor Descriptor { get; }
    public string Label => Descriptor.Label;
    public string[]? Options => Descriptor.Options;
    public bool HasOptions => Descriptor.Options is not null;
    public bool IsFreeText => Descriptor.Options is null;
    public char PasswordChar => Descriptor.Secret ? '●' : '\0';

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => _target[Descriptor.Key] = value ?? "";
}

/// <summary>A node in the project tree. The detail pane is chosen by node type.</summary>
public abstract partial class NavNode : ObservableObject
{
    protected NavNode(string title, Geometry icon)
    {
        _title = title;
        Icon = icon;
    }

    public AppState App => AppState.Instance;
    public Geometry Icon { get; }
    public ObservableCollection<NavNode> Children { get; } = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Channel and device nodes show a status dot instead of an icon.</summary>
    public virtual bool ShowStateDot => false;
    [ObservableProperty] private IBrush _stateBrush = Brushes.Gray;

    // ---- context menu ----
    private List<MenuEntry>? _menu;
    private readonly List<IRelayCommand> _menuCommands = new();

    /// <summary>Right-click menu for this node; built on first use.</summary>
    public IReadOnlyList<MenuEntry> MenuEntries
    {
        get
        {
            if (_menu is null)
            {
                _menu = CreateMenu().ToList();
                App.PropertyChanged += OnAppChanged;
            }
            return _menu;
        }
    }

    protected virtual IEnumerable<MenuEntry> CreateMenu() => [];

    protected static INavHost? Host => NavHost.Current;
    protected static bool Editable => AppState.Instance.IsEditable;

    protected MenuEntry Item(string header, Action run, Func<bool>? canExecute = null)
    {
        var cmd = canExecute is null ? new RelayCommand(run) : new RelayCommand(run, canExecute);
        _menuCommands.Add(cmd);
        return new MenuEntry(header, cmd);
    }

    protected MenuEntry ItemAsync(string header, Func<Task> run, Func<bool>? canExecute = null)
    {
        var cmd = canExecute is null ? new AsyncRelayCommand(run) : new AsyncRelayCommand(run, canExecute);
        _menuCommands.Add(cmd);
        return new MenuEntry(header, cmd);
    }

    protected static MenuEntry Sep() => MenuEntry.Separator();

    /// <summary>Re-evaluates every menu item's enabled state (editing is locked while the runtime runs).</summary>
    protected void NotifyMenu()
    {
        foreach (var c in _menuCommands) c.NotifyCanExecuteChanged();
    }

    private void OnAppChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsEditable) or nameof(AppState.IsRunning) or nameof(AppState.IsBusy))
            NotifyMenu();
    }

    protected IEnumerable<MenuEntry> ExpandCollapseItems()
    {
        yield return Item("Expand All", () => SetExpanded(this, true));
        yield return Item("Collapse All", () => SetExpanded(this, false));
    }

    private static void SetExpanded(NavNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }

    public virtual void Detach() => App.PropertyChanged -= OnAppChanged;

    protected static Geometry Res(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, null, out var res) && res is Geometry g
            ? g
            : Geometry.Parse("M0,0");

    protected static IBrush BrushFor(ConnectionState? state, bool enabled) => !enabled ? Brushes.LightGray : state switch
    {
        ConnectionState.Connected => Brushes.SeaGreen,
        ConnectionState.Connecting => Brushes.Goldenrod,
        ConnectionState.Faulted => Brushes.Firebrick,
        _ => Brushes.Gray,
    };
}

public sealed class ProjectNode : NavNode
{
    public ProjectNode() : base("Project", Res("ProjectIcon")) { }
    public string ProjectPath => App.IsRemote ? App.Backend.Description : ConfigStore.ConfigPath;
    public int DeviceCount => App.Config.Channels.Sum(c => c.Devices.Count);
    public int TagCount => App.Config.Channels.Sum(c => c.Devices.Sum(d => d.Tags.Count));

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return Item("Save Project", App.Save);
        yield return Sep();
        yield return new MenuEntry("Start Runtime", App.StartCommand);
        yield return new MenuEntry("Stop Runtime", App.StopCommand);
        yield return Sep();
        foreach (var e in ExpandCollapseItems()) yield return e;
    }
}

public sealed partial class ConnectivityNode : NavNode
{
    private readonly Action<NavNode> _select;
    private readonly Func<ChannelNode, Task> _addDevice;

    public ConnectivityNode(Action<NavNode> select, Func<ChannelNode, Task> addDevice) : base("Connectivity", Res("FolderIcon"))
    {
        _select = select;
        _addDevice = addDevice;
        var channels = AppState.Instance.Config.Channels;
        foreach (var c in channels) Children.Add(new ChannelNode(c, select, addDevice));
        channels.CollectionChanged += OnChannelsChanged;
        App.PropertyChanged += OnAppPropertyChanged;
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.IsEditable)) return;
        NewChannelCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    public override void Detach()
    {
        Config.Channels.CollectionChanged -= OnChannelsChanged;
        App.PropertyChanged -= OnAppPropertyChanged;
        foreach (var c in Children.OfType<ChannelNode>()) c.Detach();
        base.Detach();
    }

    private GatewayConfig Config => App.Config;

    public ObservableCollection<ChannelConfig> Channels => App.Config.Channels;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(RenameSelectedCommand), nameof(DuplicateSelectedCommand), nameof(DeleteSelectedCommand))]
    private ChannelConfig? _selected;

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("New Channel…", () => Host?.NewChannelAsync() ?? Task.CompletedTask, () => Editable);
        yield return Sep();
        foreach (var e in ExpandCollapseItems()) yield return e;
    }

    // Commands for the channel list's right-click menu (they act on the selected row).
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private Task NewChannelAsync() => Host?.NewChannelAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected() => Open(Selected);

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private Task RenameSelectedAsync() =>
        Selected is not null && NodeFor(Selected) is { } node && Host is not null ? Host.RenameNodeAsync(node) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private void DuplicateSelected()
    {
        if (Selected is not null && NodeFor(Selected) is { } node) Host?.DuplicateChannel(node);
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private Task DeleteSelectedAsync() =>
        Selected is not null && NodeFor(Selected) is { } node && Host is not null ? Host.DeleteNodeAsync(node) : Task.CompletedTask;

    private bool IsEditable() => Editable;
    private bool HasSelection() => Selected is not null;
    private bool CanChangeSelection() => Selected is not null && Editable;

    private void OnChannelsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ChannelConfig c in e.OldItems)
                if (NodeFor(c) is { } gone)
                {
                    gone.Detach();
                    Children.Remove(gone);
                }
        if (e.NewItems is not null)
            foreach (ChannelConfig c in e.NewItems) Children.Add(new ChannelNode(c, _select, _addDevice));
    }

    public void Open(ChannelConfig? config)
    {
        if (config is not null && NodeFor(config) is { } node) _select(node);
    }

    public ChannelNode? NodeFor(ChannelConfig c) => Children.OfType<ChannelNode>().FirstOrDefault(n => n.Config == c);
}

public sealed partial class ChannelNode : NavNode
{
    private readonly Action<NavNode> _select;
    private readonly Func<ChannelNode, Task> _addDevice;

    public ChannelNode(ChannelConfig config, Action<NavNode> select, Func<ChannelNode, Task> addDevice)
        : base(config.Name, Res("ServerIcon"))
    {
        _select = select;
        _addDevice = addDevice;
        Config = config;
        config.PropertyChanged += OnConfigChanged;
        config.Devices.CollectionChanged += OnDevicesChanged;
        App.RuntimeSynced += RefreshState;

        foreach (var d in config.Devices) AddDeviceNode(d);
        RebuildSettings();
        RefreshState();

        App.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppState.IsEditable)) return;
            OpenSelectedDeviceCommand.NotifyCanExecuteChanged();
            RenameSelectedDeviceCommand.NotifyCanExecuteChanged();
            DuplicateSelectedDeviceCommand.NotifyCanExecuteChanged();
            DeleteSelectedDeviceCommand.NotifyCanExecuteChanged();
        };
    }

    public ChannelConfig Config { get; }
    public override bool ShowStateDot => true;

    public ObservableCollection<SettingItem> Settings { get; } = new();
    public IReadOnlyList<DriverType> DriverTypes { get; } = Enum.GetValues<DriverType>();

    [ObservableProperty] private string _stateText = "Stopped";
    [ObservableProperty] private string _deviceHint = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedDeviceCommand), nameof(RenameSelectedDeviceCommand), nameof(DuplicateSelectedDeviceCommand), nameof(DeleteSelectedDeviceCommand))]
    private DeviceConfig? _selectedDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand), nameof(DuplicateSelectedDeviceCommand))]
    private bool _canAddDevice = true;

    partial void OnCanAddDeviceChanged(bool value) => NotifyMenu();

    public IEnumerable<DeviceNode> DeviceNodes => Children.OfType<DeviceNode>();
    public DeviceNode? NodeFor(DeviceConfig d) => DeviceNodes.FirstOrDefault(n => n.Config == d);

    public override void Detach()
    {
        Config.PropertyChanged -= OnConfigChanged;
        Config.Devices.CollectionChanged -= OnDevicesChanged;
        App.RuntimeSynced -= RefreshState;
        foreach (var d in DeviceNodes) d.Detach();
        base.Detach();
    }

    private MenuEntry? _toggleEntry;
    private string ToggleHeader => Config.Enabled ? "Disable Channel" : "Enable Channel";

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("New Device…", () => Host?.NewDeviceAsync(this) ?? Task.CompletedTask, () => Editable && CanAddDevice);
        yield return ItemAsync("Add Device from Template…", () => Host?.AddDeviceFromTemplateAsync(this) ?? Task.CompletedTask, () => Editable && CanAddDevice);
        yield return Sep();
        yield return ItemAsync("Rename…", () => Host?.RenameNodeAsync(this) ?? Task.CompletedTask, () => Editable);
        _toggleEntry = Item(ToggleHeader, () => Config.Enabled = !Config.Enabled, () => Editable);
        yield return _toggleEntry;
        yield return Item("Duplicate Channel", () => Host?.DuplicateChannel(this), () => Editable);
        yield return Sep();
        yield return ItemAsync("Delete Channel", () => Host?.DeleteNodeAsync(this) ?? Task.CompletedTask, () => Editable);
    }

    // Commands for the device list's right-click menu (they act on the selected row).
    [RelayCommand(CanExecute = nameof(HasSelectedDevice))]
    private void OpenSelectedDevice() => Open(SelectedDevice);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedDevice))]
    private Task RenameSelectedDeviceAsync() =>
        SelectedDevice is not null && NodeFor(SelectedDevice) is { } node && Host is not null ? Host.RenameNodeAsync(node) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDuplicateSelectedDevice))]
    private void DuplicateSelectedDevice()
    {
        if (SelectedDevice is not null && NodeFor(SelectedDevice) is { } node) Host?.DuplicateDevice(node);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedDevice))]
    private Task DeleteSelectedDeviceAsync() =>
        SelectedDevice is not null && NodeFor(SelectedDevice) is { } node && Host is not null
            ? Host.DeleteNodeAsync(node)
            : Task.CompletedTask;

    private bool HasSelectedDevice() => SelectedDevice is not null;
    private bool CanDuplicateSelectedDevice() => SelectedDevice is not null && Editable && CanAddDevice;
    private bool CanDeleteSelectedDevice() => SelectedDevice is not null && Editable;

    private void AddDeviceNode(DeviceConfig d)
    {
        var node = new DeviceNode(Config, d);
        node.PropertyChanged += OnDeviceNodeChanged;
        Children.Add(node);
    }

    private void OnDevicesChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DeviceConfig d in e.OldItems)
                if (NodeFor(d) is { } gone)
                {
                    gone.PropertyChanged -= OnDeviceNodeChanged;
                    gone.Detach();
                    Children.Remove(gone);
                }
        if (e.NewItems is not null)
            foreach (DeviceConfig d in e.NewItems) AddDeviceNode(d);
        UpdateCanAdd();
        RefreshState();
    }

    private void OnDeviceNodeChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceNode.RuntimeState)) RefreshState();
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChannelConfig.Name): Title = Config.Name; break;
            case nameof(ChannelConfig.DriverType):
                // Settings of the old driver are meaningless for the new one, on the channel and on its devices.
                Config.Settings.Clear();
                foreach (var d in Config.Devices) d.Settings.Clear();
                RebuildSettings();
                break;
            case nameof(ChannelConfig.Enabled):
                if (_toggleEntry is not null) _toggleEntry.Header = ToggleHeader;
                RefreshState();
                break;
        }
    }

    private void RebuildSettings()
    {
        Settings.Clear();
        var info = DriverCatalog.For(Config);
        foreach (var d in info.ChannelSettings) Settings.Add(new SettingItem(Config.Settings, d));
        DeviceHint = info.MaxDevices == 1
            ? "This driver reads a continuous stream, so a channel holds exactly one device."
            : info.Type is DriverType.ModbusRtu or DriverType.Rs232Poll
                ? "Devices on this channel share the serial bus (multi-drop)."
                : "";
        UpdateCanAdd();
    }

    private void UpdateCanAdd()
    {
        var max = DriverCatalog.For(Config).MaxDevices;
        CanAddDevice = max == 0 || Config.Devices.Count < max;
    }

    /// <summary>Channel state is the worst of its devices' states.</summary>
    private void RefreshState()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var states = DeviceNodes.Where(d => d.Config.Enabled).Select(d => d.RuntimeState).ToList();
            ConnectionState? state =
                states.Count == 0 || states.All(s => s is null) ? null
                : states.Any(s => s == ConnectionState.Faulted) ? ConnectionState.Faulted
                : states.Any(s => s == ConnectionState.Connecting) ? ConnectionState.Connecting
                : states.All(s => s == ConnectionState.Connected) ? ConnectionState.Connected
                : ConnectionState.Stopped;

            StateText = !Config.Enabled ? "Disabled" : state?.ToString() ?? "Stopped";
            StateBrush = BrushFor(state, Config.Enabled);
        });
    }

    /// <summary>Opens the New Device wizard for this channel.</summary>
    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private Task AddDeviceAsync() => _addDevice(this);

    public void Open(DeviceConfig? device)
    {
        if (device is not null && NodeFor(device) is { } node) _select(node);
    }
}

public sealed partial class DeviceNode : NavNode
{
    private readonly ChannelConfig _channel;
    private ConnectionRuntime? _runtime;

    public DeviceNode(ChannelConfig channel, DeviceConfig device) : base(device.Name, Res("TableIcon"))
    {
        _channel = channel;
        Config = device;
        device.PropertyChanged += OnConfigChanged;
        channel.PropertyChanged += OnChannelChanged;
        App.RuntimeSynced += RefreshState;
        RebuildSettings();
        RefreshState();
    }

    public DeviceConfig Config { get; }
    public string ChannelName => _channel.Name;
    public override bool ShowStateDot => true;

    public ObservableCollection<SettingItem> Settings { get; } = new();
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<TagAccess> Accesses { get; } = Enum.GetValues<TagAccess>();

    [ObservableProperty] private string _addressHint = "";
    [ObservableProperty] private string _stateText = "Stopped";
    [ObservableProperty] private ConnectionState? _runtimeState;
    [ObservableProperty] private bool _hasSettings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTagCommand), nameof(CopyAddressCommand))]
    private TagConfig? _selectedTag;

    // ---- commissioning: live health and a connection test ----
    [ObservableProperty] private string _diagnosticsText = "Start the runtime to see live statistics.";
    [ObservableProperty] private string _eventsText = "";
    [ObservableProperty] private string _testText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))] private bool _isTesting;

    /// <summary>Called about once a second while this device is the selected node.</summary>
    public async Task RefreshDiagnosticsAsync()
    {
        if (!App.IsRunning)
        {
            DiagnosticsText = "Start the runtime to see live statistics.";
            EventsText = "";
            return;
        }
        try
        {
            var d = (await App.Backend.GetDiagnosticsAsync()).FirstOrDefault(x =>
                string.Equals(x.Channel, _channel.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Device, Config.Name, StringComparison.OrdinalIgnoreCase));
            if (d is null) { DiagnosticsText = Config.Enabled ? "This device is not part of the running project (save and restart)." : "Disabled."; return; }

            var up = d.ConnectedSince is { } since ? $"connected for {FormatSpan(DateTimeOffset.UtcNow - since)}" : d.State.ToLowerInvariant();
            DiagnosticsText =
                $"State: {up} · {d.Reconnects} reconnect(s)\n" +
                $"Polls: {d.Polls} ({d.PollErrors} failed) · last {d.LastPollMs:0.#} ms · average {d.AveragePollMs:0.#} ms\n" +
                $"Tags: {d.GoodTags} good, {d.BadTags} not good · {d.UpdatesPerSecond:0.#} updates/s";
            var events = await App.Backend.GetEventsAsync(_channel.Name, Config.Name);
            EventsText = string.Join("\n", events.TakeLast(6).Select(e => $"{e.Time.ToLocalTime():HH:mm:ss}  {e.Level,-5} {e.Text}"));
        }
        catch (Exception ex) { DiagnosticsText = "Statistics are not available: " + ex.Message; }
    }

    private static string FormatSpan(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (App.IsRemote && App.IsDirty)
        {
            TestText = "The service tests the device as stored there. Save the project first.";
            return;
        }
        IsTesting = true;
        TestText = "Connecting…";
        try
        {
            var r = await App.Backend.TestDeviceAsync(_channel, Config);
            var lines = new List<string> { (r.Connected ? "✔ " : "✘ ") + r.Message + $" ({r.ElapsedMs} ms)" };
            foreach (var t in r.Tags)
                lines.Add($"   {t.Name} ({t.Address}): " + (t.Quality == "Good" ? $"{t.Value}" : $"{t.Quality}{(t.Error is null ? "" : " — " + t.Error)}"));
            TestText = string.Join("\n", lines);
        }
        catch (Exception ex) { TestText = "✘ " + ex.Message; }
        finally { IsTesting = false; }
    }

    private bool CanTest() => !IsTesting;

    public override void Detach()
    {
        Config.PropertyChanged -= OnConfigChanged;
        _channel.PropertyChanged -= OnChannelChanged;
        App.RuntimeSynced -= RefreshState;
        if (_runtime is not null) _runtime.PropertyChanged -= OnRuntimeChanged;
        base.Detach();
    }

    private MenuEntry? _toggleEntry;
    private string ToggleHeader => Config.Enabled ? "Disable Device" : "Enable Device";

    private bool CanDuplicate()
    {
        var max = DriverCatalog.For(_channel).MaxDevices;
        return Editable && (max == 0 || _channel.Devices.Count < max);
    }

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return Item("New Tag", () => AddTagCommand.Execute(null), () => Editable);
        yield return ItemAsync("Import Tags from CSV…", () => ImportTagsCommand.ExecuteAsync(null), () => Editable);
        yield return ItemAsync("Export Tags to CSV…", () => ExportTagsCommand.ExecuteAsync(null));
        yield return Sep();
        yield return ItemAsync("Rename…", () => Host?.RenameNodeAsync(this) ?? Task.CompletedTask, () => Editable);
        _toggleEntry = Item(ToggleHeader, () => Config.Enabled = !Config.Enabled, () => Editable);
        yield return _toggleEntry;
        yield return Item("Duplicate Device", () => Host?.DuplicateDevice(this), CanDuplicate);
        yield return ItemAsync("Save as Template…", () => Host?.SaveDeviceAsTemplateAsync(this) ?? Task.CompletedTask, () => Editable);
        yield return Sep();
        yield return ItemAsync("Delete Device", () => Host?.DeleteNodeAsync(this) ?? Task.CompletedTask, () => Editable);
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceConfig.Name)) Title = Config.Name;
        else if (e.PropertyName == nameof(DeviceConfig.Enabled))
        {
            if (_toggleEntry is not null) _toggleEntry.Header = ToggleHeader;
            RefreshState();
        }
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelConfig.DriverType)) { RebuildSettings(); NotifyMenu(); }
        else if (e.PropertyName == nameof(ChannelConfig.Name)) OnPropertyChanged(nameof(ChannelName));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTag))]
    private async Task CopyAddressAsync()
    {
        if (SelectedTag is not null && FileService.CopyText is { } copy) await copy(SelectedTag.Address);
    }

    private void RebuildSettings()
    {
        Settings.Clear();
        var info = DriverCatalog.For(_channel);
        AddressHint = info.AddressHint;
        foreach (var d in info.DeviceSettings) Settings.Add(new SettingItem(Config.Settings, d));
        HasSettings = Settings.Count > 0;
    }

    private void RefreshState()
    {
        if (_runtime is not null) _runtime.PropertyChanged -= OnRuntimeChanged;
        _runtime = App.RunningConnections.FirstOrDefault(r =>
            r.Config.ChannelName == _channel.Name && r.Config.DeviceName == Config.Name);
        if (_runtime is not null) _runtime.PropertyChanged += OnRuntimeChanged;
        OnRuntimeChanged(null, null);
    }

    private void OnRuntimeChanged(object? s, PropertyChangedEventArgs? e) => Dispatcher.UIThread.Post(() =>
    {
        var enabled = Config.Enabled && _channel.Enabled;
        RuntimeState = _runtime?.State;
        StateText = !enabled ? "Disabled"
            : _runtime is null ? "Stopped"
            : string.IsNullOrEmpty(_runtime.Message) ? _runtime.State.ToString() : $"{_runtime.State}: {_runtime.Message}";
        StateBrush = BrushFor(_runtime?.State, enabled);
    });

    [RelayCommand]
    private void AddTag()
    {
        var n = Config.Tags.Count + 1;
        while (Config.Tags.Any(t => t.Name == $"Tag{n}")) n++;
        var tag = new TagConfig { Name = $"Tag{n}" };
        Config.Tags.Add(tag);
        SelectedTag = tag;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTag))]
    private void RemoveTag()
    {
        if (SelectedTag is not null) Config.Tags.Remove(SelectedTag);
    }

    private bool HasSelectedTag() => SelectedTag is not null;

    /// <summary>Adds tags from a CSV file; tags with an existing name are updated in place.</summary>
    [RelayCommand]
    private async Task ImportTagsAsync()
    {
        if (FileService.PickOpen is null || FileService.Message is null) return;
        var path = await FileService.PickOpen("Import tags from CSV");
        if (path is null) return;

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            await FileService.Message("Import tags", $"Could not read the file:\n{ex.Message}");
            return;
        }

        var result = TagCsv.Parse(text);
        var (added, updated) = TagCsv.ApplyTo(Config, result.Tags);

        var summary = $"{added} tag(s) added, {updated} updated";
        if (result.Errors.Count > 0) summary += $", {result.Errors.Count} row(s) skipped";
        summary += $" — {Path.GetFileName(path)}";
        App.LogInfo($"[{ChannelName}.{Config.Name}] {summary}");
        foreach (var e in result.Errors) App.LogWarning($"[{ChannelName}.{Config.Name}] CSV import: {e}");

        var message = summary + ".";
        if (result.Errors.Count > 0)
        {
            message += "\n\n" + string.Join("\n", result.Errors.Take(8));
            if (result.Errors.Count > 8) message += $"\n… and {result.Errors.Count - 8} more (see the event log).";
        }
        await FileService.Message("Import tags", message);
    }

    [RelayCommand]
    private async Task ExportTagsAsync()
    {
        if (FileService.PickSave is null || FileService.Message is null) return;
        var path = await FileService.PickSave("Export tags to CSV", $"{ChannelName}.{Config.Name}-tags.csv");
        if (path is null) return;

        try
        {
            // UTF-8 with BOM so Excel opens non-ASCII names correctly.
            await File.WriteAllTextAsync(path, TagCsv.Format(Config.Tags), new System.Text.UTF8Encoding(true));
            App.LogInfo($"[{ChannelName}.{Config.Name}] Exported {Config.Tags.Count} tag(s) to {path}");
        }
        catch (Exception ex)
        {
            await FileService.Message("Export tags", $"Could not write the file:\n{ex.Message}");
        }
    }
}

public sealed partial class ServerNode : NavNode
{
    public ServerNode() : base("OPC UA Server", Res("ServerIcon")) { }

    public ServerSettings Server => App.Config.Server;

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("Copy Endpoint URL", CopyEndpointAsync, () => App.IsRunning && App.EndpointUrl.Length > 0);
        yield return Sep();
        yield return new MenuEntry("Start Runtime", App.StartCommand);
        yield return new MenuEntry("Stop Runtime", App.StopCommand);
    }

    private async Task CopyEndpointAsync()
    {
        if (FileService.CopyText is { } copy) await copy(App.EndpointUrl);
    }

    public IReadOnlyList<UserRole> Roles { get; } = Enum.GetValues<UserRole>();
    [ObservableProperty] private UserRole _newRole = UserRole.Operator;

    // ---- security review ----
    public ObservableCollection<Finding> Findings { get; } = new();
    [ObservableProperty] private string _reviewMessage = "";

    [RelayCommand]
    private async Task ReviewSecurityAsync()
    {
        Findings.Clear();
        try
        {
            foreach (var f in await App.Backend.GetSecurityReportAsync()) Findings.Add(f);
            ReviewMessage = Findings.Count == 0 ? "No findings: nothing obviously exposed." : $"{Findings.Count} finding(s), most serious first.";
        }
        catch (Exception ex) { ReviewMessage = "The review could not be made: " + ex.Message + (App.IsRemote ? " (it needs an Admin key)" : ""); }
    }

    [ObservableProperty] private string _newUsername = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _userMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveUserCommand))]
    private UserAccount? _selectedUser;

    /// <summary>Adds a user, or replaces the password if the name already exists.</summary>
    [RelayCommand]
    private async Task AddUserAsync()
    {
        var name = NewUsername.Trim();
        if (name.Length == 0 || NewPassword.Length < 8)
        {
            UserMessage = "Enter a user name and a password of at least 8 characters.";
            return;
        }

        if (App.Remote is { } remote)
        {
            // The service hashes the password; only names come back.
            try { await remote.AddUserAsync(name, NewPassword, NewRole); }
            catch (RizzServer.Core.Api.ServiceApiException ex) { UserMessage = ex.FullMessage; return; }
            UserMessage = $"User {name} saved on the service.";
        }
        else
        {
            var hash = PasswordHasher.Hash(NewPassword);
            var existing = Server.Users.FirstOrDefault(u => u.Username == name);
            if (existing is not null) { existing.PasswordHash = hash; existing.Role = NewRole; UserMessage = $"Password and role updated for {name}."; }
            else { Server.Users.Add(new UserAccount { Username = name, PasswordHash = hash, Role = NewRole }); UserMessage = $"User {name} added as {NewRole}."; }
            App.Save();
        }

        NewUsername = "";
        NewPassword = "";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedUser))]
    private async Task RemoveUserAsync()
    {
        if (SelectedUser is null) return;
        var name = SelectedUser.Username;

        if (App.Remote is { } remote)
        {
            try { await remote.RemoveUserAsync(name); }
            catch (RizzServer.Core.Api.ServiceApiException ex) { UserMessage = ex.FullMessage; return; }
        }
        else
        {
            Server.Users.Remove(SelectedUser);
            App.Save();
        }
        UserMessage = $"User {name} removed.";
    }

    private bool HasSelectedUser() => SelectedUser is not null;
}

public sealed partial class QuickClientNode : NavNode
{
    public QuickClientNode() : base("Quick Client", Res("TableIcon")) { }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(CopyItemIdCommand), nameof(CopyValueCommand))]
    private TagRuntime? _selected;

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return new MenuEntry("Start Runtime", App.StartCommand);
        yield return new MenuEntry("Stop Runtime", App.StopCommand);
    }

    // Right-click menu of the live-values grid
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyItemIdAsync()
    {
        if (Selected is not null && FileService.CopyText is { } copy) await copy(Selected.NodePath);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyValueAsync()
    {
        if (Selected is not null && FileService.CopyText is { } copy)
            await copy(Convert.ToString(Selected.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    private bool HasSelection() => Selected is not null;

    [ObservableProperty] private string _writeText = "";
    [ObservableProperty] private string _message = "";

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (Selected is null) return;
        try
        {
            await App.WriteAsync(Selected, WriteText);
            Message = $"Wrote {WriteText} to {Selected.NodePath}";
        }
        catch (Exception ex) { Message = "Write failed: " + ex.Message; }
    }

    private bool CanWrite() => Selected?.Config.Access == TagAccess.ReadWrite;
}
