using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, INavHost
{
    private ConnectivityNode _connectivity = null!;

    public MainViewModel()
    {
        BuildTree();
        NavHost.Current = this;

        App.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsEditable)) NotifyCommands();
            if (e.PropertyName is nameof(AppState.IsRunning) or nameof(AppState.IsRemote) or nameof(AppState.ConnectionText)) NotifyConnectionCommands();
        };

        // Connecting to / leaving a service swaps the project object, so the tree is rebuilt.
        App.ConfigReplaced += () =>
        {
            BuildTree();
            NotifyCommands();
            NotifyConnectionCommands();
        };
    }

    private void BuildTree()
    {
        foreach (var old in Roots) DetachTree(old);
        Roots.Clear();

        var project = new ProjectNode();
        _connectivity = new ConnectivityNode(n => SelectedNode = n, RunDeviceWizardAsync);
        project.Children.Add(_connectivity);
        project.Children.Add(new ServerNode());
        project.Children.Add(new QuickClientNode());
        Roots.Add(project);
        SelectedNode = project;
    }

    private static void DetachTree(NavNode node)
    {
        foreach (var child in node.Children) DetachTree(child);
        node.Detach();
    }

    public AppState App => AppState.Instance;
    public ObservableCollection<NavNode> Roots { get; } = new();

    /// <summary>Asks the user to confirm; set by the window.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Like <see cref="Confirm"/> with a custom button caption (e.g. "Discard").</summary>
    public Func<string, string, Task<bool>>? ConfirmWith { get; set; }

    /// <summary>Shows the connect dialog; true when a service was connected.</summary>
    public Func<Task<bool>>? ShowConnect { get; set; }
    public Func<Task>? ShowAbout { get; set; }
    public Action? Exit { get; set; }

    [ObservableProperty] private NavNode? _selectedNode;
    [ObservableProperty] private bool _logVisible = UiSettings.Instance.LogVisible;

    partial void OnLogVisibleChanged(bool value)
    {
        UiSettings.Instance.LogVisible = value;
        UiSettings.Instance.Save();
    }

    // ---- View ▸ Theme: System / Light / Dark ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark))]
    private ThemeMode _theme = UiSettings.Instance.Theme;

    public bool IsThemeSystem => Theme == ThemeMode.System;
    public bool IsThemeLight => Theme == ThemeMode.Light;
    public bool IsThemeDark => Theme == ThemeMode.Dark;

    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (!Enum.TryParse<ThemeMode>(name, out var mode)) return;
        Theme = mode;
        ThemeService.Set(mode);
    }

    private ChannelNode? _watched;

    partial void OnSelectedNodeChanged(NavNode? value)
    {
        // The channel's "can add device" flag changes with its device count.
        if (_watched is not null) _watched.PropertyChanged -= OnWatchedChannelChanged;
        _watched = CurrentChannel;
        if (_watched is not null) _watched.PropertyChanged += OnWatchedChannelChanged;
        NotifyCommands();
    }

    private void OnWatchedChannelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelNode.CanAddDevice)) NotifyCommands();
    }

    private void NotifyCommands()
    {
        AddChannelCommand.NotifyCanExecuteChanged();
        AddDeviceCommand.NotifyCanExecuteChanged();
        AddTagCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        ImportTagsCommand.NotifyCanExecuteChanged();
        ExportTagsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The channel the selection belongs to (the channel itself or one of its devices).</summary>
    private ChannelNode? CurrentChannel => SelectedNode switch
    {
        ChannelNode c => c,
        DeviceNode d => _connectivity.Children.OfType<ChannelNode>().FirstOrDefault(c => c.Children.Contains(d)),
        _ => null,
    };

    private DeviceNode? CurrentDevice => SelectedNode as DeviceNode;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await App.SaveAsync() && FileService.Message is { } message)
            await message("Save failed", App.LastSaveError);
    }

    // ---- File ▸ Connect to / Disconnect from / Reload from a headless service ----
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (ShowConnect is not null) await ShowConnect();
    }

    private bool CanConnect() => !App.IsRemote && !App.IsRunning;

    [RelayCommand(CanExecute = nameof(IsRemoteNow))]
    private async Task DisconnectAsync()
    {
        if (!await ConfirmDiscardAsync("Disconnect from the service")) return;
        App.DisconnectRemote();
    }

    [RelayCommand(CanExecute = nameof(IsRemoteNow))]
    private async Task ReloadAsync()
    {
        if (App.Remote is not { } remote || !await ConfirmDiscardAsync("Reload from the service")) return;
        try
        {
            await remote.ReloadAsync();
            SelectedNode = Roots[0];
            App.LogInfo("Reloaded the project from " + remote.Description);
        }
        catch (Exception ex) when (ex is RizzServer.Core.Api.ServiceApiException)
        {
            if (FileService.Message is { } message) await message("Reload failed", ((RizzServer.Core.Api.ServiceApiException)ex).FullMessage);
        }
    }

    private bool IsRemoteNow() => App.IsRemote;

    /// <summary>True when there is nothing unsaved, or the user agreed to throw it away.</summary>
    private async Task<bool> ConfirmDiscardAsync(string action)
    {
        if (!App.IsDirty) return true;
        return ConfirmWith is null || await ConfirmWith($"{action}? Changes that are not saved to the service will be lost.", "Discard");
    }

    private void NotifyConnectionCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows a wizard; returns true when the user finished it. Set by the window.</summary>
    public Func<WizardViewModel, Task<bool>>? ShowWizard { get; set; }

    /// <summary>New Channel wizard: channel + its first device.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task AddChannelAsync()
    {
        if (ShowWizard is null) return;
        var wizard = WizardViewModel.ForNewChannel(App.Config);
        if (!await ShowWizard(wizard)) return;

        var (channel, device) = wizard.Commit();
        if (_connectivity.NodeFor(channel) is { } channelNode)
            SelectedNode = (NavNode?)channelNode.NodeFor(device) ?? channelNode;
    }

    /// <summary>New Device wizard for an existing channel.</summary>
    private async Task RunDeviceWizardAsync(ChannelNode channel)
    {
        if (ShowWizard is null) return;
        var wizard = WizardViewModel.ForNewDevice(App.Config, channel.Config);
        if (!await ShowWizard(wizard)) return;

        var (_, device) = wizard.Commit();
        if (channel.NodeFor(device) is { } node) SelectedNode = node;
    }

    // ---- INavHost: used by the tree's right-click menus ----
    public void Select(NavNode node) => SelectedNode = node;
    public Task NewChannelAsync() => AddChannelAsync();
    public Task NewDeviceAsync(ChannelNode channel) => RunDeviceWizardAsync(channel);

    private ChannelNode? ChannelOf(DeviceNode device) =>
        _connectivity.Children.OfType<ChannelNode>().FirstOrDefault(c => c.Children.Contains(device));

    private static string UniqueCopyName(string name, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = $"{name} Copy";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name} Copy {n}";
        return candidate;
    }

    public void DuplicateChannel(ChannelNode channel)
    {
        if (!App.IsEditable) return;
        var clone = ConfigStore.Clone(channel.Config);
        clone.Name = UniqueCopyName(channel.Config.Name, App.Config.Channels.Select(c => c.Name));
        App.Config.Channels.Add(clone);
        if (_connectivity.NodeFor(clone) is { } node) SelectedNode = node;
    }

    public void DuplicateDevice(DeviceNode device)
    {
        if (!App.IsEditable || ChannelOf(device) is not { } channel || !channel.CanAddDevice) return;
        var clone = ConfigStore.Clone(device.Config);
        clone.Name = UniqueCopyName(device.Config.Name, channel.Config.Devices.Select(d => d.Name));
        channel.Config.Devices.Add(clone);
        if (channel.NodeFor(clone) is { } node) SelectedNode = node;
    }

    /// <summary>Asks for a new name; returns null if cancelled. Set by the window.</summary>
    public Func<string, string, string, Func<string, string?>, Task<string?>>? Prompt { get; set; }

    public async Task RenameNodeAsync(NavNode node)
    {
        if (!App.IsEditable || Prompt is null) return;
        switch (node)
        {
            case ChannelNode channel:
            {
                var name = await Prompt("Rename Channel", "Channel name:", channel.Config.Name,
                    v => NameRules.Validate(v, "channel", App.Config.Channels.Where(c => c != channel.Config).Select(c => c.Name)));
                if (name is not null) channel.Config.Name = name;
                break;
            }
            case DeviceNode device when ChannelOf(device) is { } owner:
            {
                var name = await Prompt("Rename Device", "Device name:", device.Config.Name,
                    v => NameRules.Validate(v, "device", owner.Config.Devices.Where(d => d != device.Config).Select(d => d.Name)));
                if (name is not null) device.Config.Name = name;
                break;
            }
        }
    }

    public async Task DeleteNodeAsync(NavNode node)
    {
        if (!App.IsEditable) return;
        switch (node)
        {
            case DeviceNode device when ChannelOf(device) is { } channel:
                if (Confirm is not null && !await Confirm($"Delete device '{device.Title}' and its {device.Config.Tags.Count} tag(s)?")) return;
                if (SelectedNode == device) SelectedNode = channel;
                channel.Config.Devices.Remove(device.Config);
                break;
            case ChannelNode channel:
                if (Confirm is not null && !await Confirm($"Delete channel '{channel.Title}' with {channel.Config.Devices.Count} device(s)?")) return;
                if (SelectedNode == channel || SelectedNode is DeviceNode d && channel.Children.Contains(d)) SelectedNode = _connectivity;
                App.Config.Channels.Remove(channel.Config);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private async Task AddDeviceAsync()
    {
        if (CurrentChannel is { } channel) await channel.AddDeviceCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private void AddTag() => CurrentDevice?.AddTagCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task RenameAsync() => SelectedNode is { } node ? RenameNodeAsync(node) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => SelectedNode is { } node ? DeleteNodeAsync(node) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private async Task ImportTagsAsync()
    {
        if (CurrentDevice is { } device) await device.ImportTagsCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(HasDevice))]
    private async Task ExportTagsAsync()
    {
        if (CurrentDevice is { } device) await device.ExportTagsCommand.ExecuteAsync(null);
    }

    private bool HasDevice() => CurrentDevice is not null;

    private bool IsEditable() => App.IsEditable;
    private bool CanAddDevice() => App.IsEditable && CurrentChannel is { CanAddDevice: true };
    private bool CanAddTag() => App.IsEditable && CurrentDevice is not null;
    private bool CanDelete() => App.IsEditable && SelectedNode is ChannelNode or DeviceNode;

    [RelayCommand]
    private void ToggleLog() => LogVisible = !LogVisible;

    // ---- event log right-click menu ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyLogEntryCommand))]
    private LogEntry? _selectedLog;

    [RelayCommand(CanExecute = nameof(HasSelectedLog))]
    private async Task CopyLogEntryAsync()
    {
        if (SelectedLog is { } e && FileService.CopyText is { } copy)
            await copy($"{e.Date} {e.Clock}	{e.Category}	{e.Message}");
    }

    private bool HasSelectedLog() => SelectedLog is not null;

    [RelayCommand]
    private void ClearLog() => App.Logs.Entries.Clear();

    [RelayCommand]
    private async Task AboutAsync()
    {
        if (ShowAbout is not null) await ShowAbout();
    }

    [RelayCommand]
    private void ExitApp() => Exit?.Invoke();
}
