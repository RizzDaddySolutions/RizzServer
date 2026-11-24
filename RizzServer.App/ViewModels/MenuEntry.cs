using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RizzServer.App.ViewModels;

/// <summary>One line of a context menu. A header of "-" renders as a separator.</summary>
public sealed partial class MenuEntry : ObservableObject
{
    public MenuEntry(string header, ICommand? command = null)
    {
        _header = header;
        Command = command;
    }

    [ObservableProperty] private string _header;

    public ICommand? Command { get; }

    public static MenuEntry Separator() => new("-");
}

/// <summary>What the tree's context menus need from the main window (dialogs, selection, destructive actions).</summary>
public interface INavHost
{
    void Select(NavNode node);
    Task NewChannelAsync();
    Task NewDeviceAsync(ChannelNode channel);
    Task DeleteNodeAsync(NavNode node);
    Task RenameNodeAsync(NavNode node);
    void DuplicateChannel(ChannelNode channel);
    void DuplicateDevice(DeviceNode device);
}

/// <summary>Set by the main view model so nodes can reach it without a reference chain.</summary>
public static class NavHost
{
    public static INavHost? Current { get; set; }
}
