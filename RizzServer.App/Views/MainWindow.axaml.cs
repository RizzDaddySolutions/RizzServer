using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using RizzServer.App.ViewModels;

namespace RizzServer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Wire();

        // Keep the newest event visible.
        AppState.Instance.Logs.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            Dispatcher.UIThread.Post(() =>
            {
                var entries = AppState.Instance.Logs.Entries;
                if (entries.Count > 0 && this.FindControl<DataGrid>("LogGrid") is { } grid)
                    grid.ScrollIntoView(entries[^1], null);
            }, DispatcherPriority.Background);
        };
    }

    private void Wire()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Confirm = text => Dialogs.ConfirmAsync(this, text);
        vm.ShowAbout = () => Dialogs.MessageAsync(this, "About RizzServer",
            "RizzServer — OPC UA gateway\nModbus · RS-232 · MQTT · OPC DA · OPC UA");
        vm.Exit = Close;
    }
}

internal static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string text)
    {
        var result = false;
        var dlg = Build("Confirm", text, owner, out var buttons);
        var yes = new Button { Content = "Delete", MinWidth = 80 };
        var no = new Button { Content = "Cancel", MinWidth = 80 };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        await dlg.ShowDialog(owner);
        return result;
    }

    public static async Task MessageAsync(Window owner, string title, string text)
    {
        var dlg = Build(title, text, owner, out var buttons);
        var ok = new Button { Content = "OK", MinWidth = 80 };
        ok.Click += (_, _) => dlg.Close();
        buttons.Children.Add(ok);
        await dlg.ShowDialog(owner);
    }

    private static Window Build(string title, string text, Window owner, out StackPanel buttons)
    {
        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 });
        panel.Children.Add(buttons);
        return new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }
}
