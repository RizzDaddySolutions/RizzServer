using System.Collections.ObjectModel;
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
        _connectivity = new ConnectivityNode(n => SelectedNode = n);
        project.Children.Add(_connectivity);
        project.Children.Add(new ServerNode());
        project.Children.Add(new QuickClientNode());
        Roots.Add(project);
        _selectedNode = project;

        App.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppState.IsEditable)) return;
            AddConnectionCommand.NotifyCanExecuteChanged();
            AddTagCommand.NotifyCanExecuteChanged();
            DeleteConnectionCommand.NotifyCanExecuteChanged();
        };
    }

    public AppState App => AppState.Instance;
    public ObservableCollection<NavNode> Roots { get; } = new();

    /// <summary>Asks the user to confirm; set by the window.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }
    public Func<Task>? ShowAbout { get; set; }
    public Action? Exit { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTagCommand), nameof(DeleteConnectionCommand))]
    private NavNode? _selectedNode;

    [ObservableProperty] private bool _logVisible = true;

    private ConnectionNode? CurrentConnection => SelectedNode as ConnectionNode;

    [RelayCommand]
    private void Save() => App.Save();

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddConnection()
    {
        var n = 1;
        while (App.Config.Connections.Any(c => c.Name == $"Channel{n}")) n++;
        var conn = new ConnectionConfig { Name = $"Channel{n}" };
        App.Config.Connections.Add(conn);
        if (_connectivity.NodeFor(conn) is { } node) SelectedNode = node;
    }

    [RelayCommand(CanExecute = nameof(CanEditConnection))]
    private void AddTag() => CurrentConnection?.AddTagCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(CanEditConnection))]
    private async Task DeleteConnectionAsync()
    {
        if (CurrentConnection is not { } node) return;
        if (Confirm is not null && !await Confirm($"Delete '{node.Title}' and its {node.Config.Tags.Count} tag(s)?")) return;
        SelectedNode = _connectivity;
        App.Config.Connections.Remove(node.Config);
    }

    private bool IsEditable() => App.IsEditable;
    private bool CanEditConnection() => CurrentConnection is not null && App.IsEditable;

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
