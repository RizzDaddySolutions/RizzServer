using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App;

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

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => _conn.Settings[Descriptor.Key] = value ?? "";
}

public sealed partial class ConnectionsViewModel : ObservableObject
{
    public AppState App => AppState.Instance;
    public ObservableCollection<ConnectionConfig> Connections => App.Config.Connections;
    public ObservableCollection<SettingItem> Settings { get; } = new();

    public IReadOnlyList<DriverType> DriverTypes { get; } = Enum.GetValues<DriverType>();
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<TagAccess> Accesses { get; } = Enum.GetValues<TagAccess>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveConnectionCommand), nameof(AddTagCommand))]
    private ConnectionConfig? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTagCommand))]
    private TagConfig? _selectedTag;

    [ObservableProperty] private string _addressHint = "";

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(ConnectionConfig? oldValue, ConnectionConfig? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnConnectionChanged;
        if (newValue is not null) newValue.PropertyChanged += OnConnectionChanged;
        RebuildSettings();
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionConfig.DriverType) || Selected is null) return;
        Selected.Settings.Clear();
        RebuildSettings();
    }

    private void RebuildSettings()
    {
        Settings.Clear();
        if (Selected is null) { AddressHint = ""; return; }
        var info = DriverCatalog.Get(Selected.DriverType);
        AddressHint = "Address: " + info.AddressHint;
        foreach (var d in info.Settings) Settings.Add(new SettingItem(Selected, d));
    }

    [RelayCommand]
    private void AddConnection()
    {
        var n = 1;
        while (Connections.Any(c => c.Name == $"Connection{n}")) n++;
        var conn = new ConnectionConfig { Name = $"Connection{n}" };
        Connections.Add(conn);
        Selected = conn;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveConnection()
    {
        if (Selected is null) return;
        var c = Selected;
        Selected = null;
        Connections.Remove(c);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddTag()
    {
        if (Selected is null) return;
        var n = Selected.Tags.Count + 1;
        while (Selected.Tags.Any(t => t.Name == $"Tag{n}")) n++;
        var tag = new TagConfig { Name = $"Tag{n}" };
        Selected.Tags.Add(tag);
        SelectedTag = tag;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTag))]
    private void RemoveTag()
    {
        if (Selected is null || SelectedTag is null) return;
        Selected.Tags.Remove(SelectedTag);
    }

    private bool HasSelectedTag() => SelectedTag is not null;

    [RelayCommand]
    private void Save() => App.Save();
}

public sealed partial class LiveDataViewModel : ObservableObject
{
    public AppState App => AppState.Instance;

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

public sealed partial class SettingsViewModel : ObservableObject
{
    public AppState App => AppState.Instance;
    public ServerSettings Server => App.Config.Server;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveUserCommand))]
    private UserAccount? _selectedUser;

    [ObservableProperty] private string _userMessage = "";

    [RelayCommand]
    private void Save() => App.Save();

    /// <summary>Adds a user, or replaces the password if the name already exists.</summary>
    public bool AddUser(string username, string password)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length < 8)
        {
            UserMessage = "Enter a user name and a password of at least 8 characters.";
            return false;
        }

        var hash = PasswordHasher.Hash(password);
        var existing = Server.Users.FirstOrDefault(u => u.Username == username);
        if (existing is not null)
        {
            existing.PasswordHash = hash;
            UserMessage = $"Password updated for {username}.";
        }
        else
        {
            Server.Users.Add(new UserAccount { Username = username, PasswordHash = hash });
            UserMessage = $"User {username} added.";
        }
        App.Save();
        return true;
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
