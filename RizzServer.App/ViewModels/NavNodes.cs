using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

/// <summary>One editable driver setting, written straight back to the connection's settings dictionary.</summary>
public sealed partial class SettingItem : ObservableObject
{
    private readonly ConnectionConfig _conn;

    public SettingItem(ConnectionConfig conn, SettingDescriptor descriptor)
    {
        _conn = conn;
        Descriptor = descriptor;
        _value = conn.Settings.TryGetValue(descriptor.Key, out var v) ? v : descriptor.Default;
        conn.Settings[descriptor.Key] = _value;
    }

    public SettingDescriptor Descriptor { get; }
    public string Label => Descriptor.Label;
    public string[]? Options => Descriptor.Options;
    public bool HasOptions => Descriptor.Options is not null;
    public bool IsFreeText => Descriptor.Options is null;
    public char PasswordChar => Descriptor.Secret ? '●' : '\0';

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => _conn.Settings[Descriptor.Key] = value ?? "";
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

    /// <summary>Connection nodes show a status dot instead of an icon.</summary>
    public virtual bool ShowStateDot => false;
    [ObservableProperty] private IBrush _stateBrush = Brushes.Gray;

    protected static Geometry Res(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, null, out var res) && res is Geometry g
            ? g
            : Geometry.Parse("M0,0");
}

public sealed class ProjectNode : NavNode
{
    public ProjectNode() : base("Project", Res("ProjectIcon")) { }
    public string ProjectPath => ConfigStore.ConfigPath;
}

public sealed partial class ConnectivityNode : NavNode
{
    private readonly Action<NavNode> _select;

    public ConnectivityNode(Action<NavNode> select) : base("Connectivity", Res("FolderIcon"))
    {
        _select = select;
        var conns = AppState.Instance.Config.Connections;
        foreach (var c in conns) Children.Add(new ConnectionNode(c));
        conns.CollectionChanged += OnConnectionsChanged;
    }

    public ObservableCollection<ConnectionConfig> Connections => App.Config.Connections;

    [ObservableProperty] private ConnectionConfig? _selected;

    private void OnConnectionsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ConnectionConfig c in e.OldItems)
                if (Children.OfType<ConnectionNode>().FirstOrDefault(n => n.Config == c) is { } gone)
                {
                    gone.Detach();
                    Children.Remove(gone);
                }
        if (e.NewItems is not null)
            foreach (ConnectionConfig c in e.NewItems) Children.Add(new ConnectionNode(c));
    }

    public void Open(ConnectionConfig? config)
    {
        if (config is null) return;
        if (Children.OfType<ConnectionNode>().FirstOrDefault(n => n.Config == config) is { } node) _select(node);
    }

    public ConnectionNode? NodeFor(ConnectionConfig c) => Children.OfType<ConnectionNode>().FirstOrDefault(n => n.Config == c);
}

public sealed partial class ConnectionNode : NavNode
{
    private ConnectionRuntime? _runtime;

    public ConnectionNode(ConnectionConfig config) : base(config.Name, Res("ServerIcon"))
    {
        Config = config;
        config.PropertyChanged += OnConfigChanged;
        App.RuntimeSynced += RefreshState;
        RebuildSettings();
        RefreshState();
    }

    public ConnectionConfig Config { get; }
    public override bool ShowStateDot => true;

    public ObservableCollection<SettingItem> Settings { get; } = new();
    public IReadOnlyList<DriverType> DriverTypes { get; } = Enum.GetValues<DriverType>();
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<TagAccess> Accesses { get; } = Enum.GetValues<TagAccess>();

    [ObservableProperty] private string _addressHint = "";
    [ObservableProperty] private string _stateText = "Stopped";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTagCommand))]
    private TagConfig? _selectedTag;

    public void Detach()
    {
        Config.PropertyChanged -= OnConfigChanged;
        App.RuntimeSynced -= RefreshState;
        if (_runtime is not null) _runtime.PropertyChanged -= OnRuntimeChanged;
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionConfig.Name)) Title = Config.Name;
        else if (e.PropertyName == nameof(ConnectionConfig.DriverType))
        {
            Config.Settings.Clear();
            RebuildSettings();
        }
        else if (e.PropertyName == nameof(ConnectionConfig.Enabled)) RefreshState();
    }

    private void RebuildSettings()
    {
        Settings.Clear();
        var info = DriverCatalog.Get(Config.DriverType);
        AddressHint = info.AddressHint;
        foreach (var d in info.Settings) Settings.Add(new SettingItem(Config, d));
    }

    private void RefreshState()
    {
        if (_runtime is not null) _runtime.PropertyChanged -= OnRuntimeChanged;
        _runtime = App.RunningConnections.FirstOrDefault(r => r.Config == Config);
        if (_runtime is not null) _runtime.PropertyChanged += OnRuntimeChanged;
        OnRuntimeChanged(null, null!);
    }

    private void OnRuntimeChanged(object? s, PropertyChangedEventArgs? e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var state = _runtime?.State;
            StateText = !Config.Enabled ? "Disabled"
                : state is null ? "Stopped"
                : string.IsNullOrEmpty(_runtime!.Message) ? state.ToString()! : $"{state}: {_runtime.Message}";
            StateBrush = !Config.Enabled ? Brushes.LightGray : state switch
            {
                ConnectionState.Connected => Brushes.SeaGreen,
                ConnectionState.Connecting => Brushes.Goldenrod,
                ConnectionState.Faulted => Brushes.Firebrick,
                _ => Brushes.Gray,
            };
        });
    }

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
}

public sealed partial class ServerNode : NavNode
{
    public ServerNode() : base("OPC UA Server", Res("ServerIcon")) { }

    public ServerSettings Server => App.Config.Server;

    [ObservableProperty] private string _newUsername = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _userMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveUserCommand))]
    private UserAccount? _selectedUser;

    /// <summary>Adds a user, or replaces the password if the name already exists.</summary>
    [RelayCommand]
    private void AddUser()
    {
        var name = NewUsername.Trim();
        if (name.Length == 0 || NewPassword.Length < 8)
        {
            UserMessage = "Enter a user name and a password of at least 8 characters.";
            return;
        }

        var hash = PasswordHasher.Hash(NewPassword);
        var existing = Server.Users.FirstOrDefault(u => u.Username == name);
        if (existing is not null) { existing.PasswordHash = hash; UserMessage = $"Password updated for {name}."; }
        else { Server.Users.Add(new UserAccount { Username = name, PasswordHash = hash }); UserMessage = $"User {name} added."; }

        NewUsername = "";
        NewPassword = "";
        App.Save();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedUser))]
    private void RemoveUser()
    {
        if (SelectedUser is null) return;
        UserMessage = $"User {SelectedUser.Username} removed.";
        Server.Users.Remove(SelectedUser);
        App.Save();
    }

    private bool HasSelectedUser() => SelectedUser is not null;
}

public sealed partial class QuickClientNode : NavNode
{
    public QuickClientNode() : base("Quick Client", Res("TableIcon")) { }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private TagRuntime? _selected;

    [ObservableProperty] private string _writeText = "";
    [ObservableProperty] private string _message = "";

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (Selected is null) return;
        try
        {
            await App.Engine.WriteAsync(Selected, WriteText);
            Message = $"Wrote {WriteText} to {Selected.NodePath}";
        }
        catch (Exception ex) { Message = "Write failed: " + ex.Message; }
    }

    private bool CanWrite() => Selected?.Config.Access == TagAccess.ReadWrite;
}
