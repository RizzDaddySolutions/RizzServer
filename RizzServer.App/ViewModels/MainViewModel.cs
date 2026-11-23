using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectivityNode _connectivity;

    public MainViewModel()
    {
        var project = new ProjectNode();
        _connectivity = new ConnectivityNode(n => SelectedNode = n, RunDeviceWizardAsync);
        project.Children.Add(_connectivity);
        project.Children.Add(new ServerNode());
        project.Children.Add(new QuickClientNode());
        Roots.Add(project);
        _selectedNode = project;

        App.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsEditable)) NotifyCommands();
        };
    }

    public AppState App => AppState.Instance;
    public ObservableCollection<NavNode> Roots { get; } = new();

    /// <summary>Asks the user to confirm; set by the window.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }
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
    private void Save() => App.Save();

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

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private async Task AddDeviceAsync()
    {
        if (CurrentChannel is { } channel) await channel.AddDeviceCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private void AddTag() => CurrentDevice?.AddTagCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        switch (SelectedNode)
        {
            case DeviceNode device when CurrentChannel is { } channel:
                if (Confirm is not null && !await Confirm($"Delete device '{device.Title}' and its {device.Config.Tags.Count} tag(s)?")) return;
                SelectedNode = channel;
                channel.Config.Devices.Remove(device.Config);
                break;
            case ChannelNode channel:
                if (Confirm is not null && !await Confirm($"Delete channel '{channel.Title}' with {channel.Config.Devices.Count} device(s)?")) return;
                SelectedNode = _connectivity;
                App.Config.Channels.Remove(channel.Config);
                break;
        }
    }

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

    [RelayCommand]
    private async Task AboutAsync()
    {
        if (ShowAbout is not null) await ShowAbout();
    }

    [RelayCommand]
    private void ExitApp() => Exit?.Invoke();
}
