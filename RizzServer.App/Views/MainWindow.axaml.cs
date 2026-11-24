using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RizzServer.App.ViewModels;

namespace RizzServer.App.Views;

public partial class MainWindow : Window
{
    private double _logHeight;

    public MainWindow()
    {
        InitializeComponent();
        WindowPlacementService.Attach(this, "Main");
        ApplyPanes();
        DataContextChanged += (_, _) => Wire();
        Closing += (_, _) => SavePanes();
        Closing += OnClosingConfirmRemote;
        LogSplitter.DragCompleted += (_, _) => SavePanes();
        TreeSplitter.DragCompleted += (_, _) => SavePanes();

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

    private bool _closeApproved;

    /// <summary>Closing with edits that were never pushed to a remote service asks first.</summary>
    private async void OnClosingConfirmRemote(object? sender, WindowClosingEventArgs e)
    {
        var app = AppState.Instance;
        if (_closeApproved || !app.IsRemote || !app.IsDirty) return;

        e.Cancel = true;
        if (await Dialogs.ConfirmAsync(this, "Close RizzServer? Changes that are not saved to the service will be lost.", "Discard"))
        {
            _closeApproved = true;
            Close();
        }
    }

    /// <summary>Restores the tree width and event-log height from the last run.</summary>
    private void ApplyPanes()
    {
        var s = UiSettings.Instance;
        TopArea.ColumnDefinitions[0].Width = new GridLength(Math.Max(120, s.TreeWidth));
        MainArea.RowDefinitions[0].MinHeight = 120;
        MainArea.RowDefinitions[2].MinHeight = 60;
        _logHeight = Math.Max(60, s.LogHeight);
        ApplyLogVisibility(s.LogVisible);
    }

    private void ApplyLogVisibility(bool visible)
    {
        var rows = MainArea.RowDefinitions;
        if (!visible && rows[2].ActualHeight > 0) _logHeight = rows[2].ActualHeight;
        rows[1].Height = new GridLength(visible ? 4 : 0);
        rows[2].MinHeight = visible ? 60 : 0; // a MinHeight would keep the hidden row open
        rows[2].Height = new GridLength(visible ? _logHeight : 0);
        LogSplitter.IsVisible = visible;
        LogPanel.IsVisible = visible;
    }

    private void SavePanes()
    {
        var s = UiSettings.Instance;
        var tree = TopArea.ColumnDefinitions[0].ActualWidth;
        if (tree > 0) s.TreeWidth = tree;
        var log = MainArea.RowDefinitions[2].ActualHeight;
        if (LogPanel.IsVisible && log > 0) { s.LogHeight = log; _logHeight = log; }
        s.Save();
    }

    private void Wire()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogVisible)) ApplyLogVisibility(vm.LogVisible);
        };
        vm.Confirm = text => Dialogs.ConfirmAsync(this, text);
        vm.ConfirmWith = (text, button) => Dialogs.ConfirmAsync(this, text, button);
        vm.ShowConnect = async () => await new ConnectWindow().ShowDialog<bool>(this);
        vm.Prompt = (title, prompt, initial, validate) => Dialogs.PromptAsync(this, title, prompt, initial, validate);
        vm.ShowAbout = () => new AboutWindow().ShowDialog(this);
        vm.ShowWizard = wizard => WizardWindow.RunAsync(this, wizard);

        FileService.CopyText = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };

        var csv = new FilePickerFileType("CSV files") { Patterns = ["*.csv"] };
        FileService.Message = (title, text) => Dialogs.MessageAsync(this, title, text);
        FileService.PickOpen = async title =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [csv, FilePickerFileTypes.All],
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        };
        FileService.PickSave = async (title, suggested) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggested,
                DefaultExtension = "csv",
                FileTypeChoices = [csv],
            });
            return file?.TryGetLocalPath();
        };
        vm.Exit = Close;
    }
}

public static class Dialogs
{
    /// <summary>Single-line text prompt. OK is rejected (with the message shown) until <paramref name="validate"/> returns null.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string prompt, string initial, Func<string, string?> validate)
    {
        string? result = null;
        var dlg = Build(title, prompt, owner, out var buttons);
        var box = new TextBox { Text = initial, MinWidth = 340, Name = "PromptBox" };
        var error = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.IndianRed,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 380,
            IsVisible = false,
            Name = "PromptError",
        };
        var panel = (StackPanel)dlg.Content!;
        panel.Children.Insert(1, box);
        panel.Children.Insert(2, error);

        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Name = "PromptOk" };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Name = "PromptCancel" };
        ok.Click += (_, _) =>
        {
            var text = (box.Text ?? "").Trim();
            if (validate(text) is { } message)
            {
                error.Text = message;
                error.IsVisible = true;
                box.Focus();
                return;
            }
            result = text;
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        await dlg.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string text, string button = "Delete")
    {
        var result = false;
        var dlg = Build("Confirm", text, owner, out var buttons);
        var yes = new Button { Content = button, MinWidth = 80 };
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
