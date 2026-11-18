using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using RizzServer.App.Pages;

namespace RizzServer.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        SystemThemeWatcher.Watch(this);
        InitializeComponent();
        Loaded += (_, _) => Nav.Navigate(typeof(DashboardPage));
    }
}
