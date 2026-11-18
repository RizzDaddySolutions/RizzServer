using System.Windows.Controls;

namespace RizzServer.App.Pages;

// Pages are created by the NavigationView, so each wires its own view model.
public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        DataContext = new DashboardViewModel();
        InitializeComponent();
    }
}

public partial class ConnectionsPage : Page
{
    public ConnectionsPage()
    {
        DataContext = new ConnectionsViewModel();
        InitializeComponent();
    }
}

public partial class LiveDataPage : Page
{
    public LiveDataPage()
    {
        DataContext = new LiveDataViewModel();
        InitializeComponent();
    }
}

public partial class LogPage : Page
{
    public LogPage()
    {
        DataContext = AppState.Instance;
        InitializeComponent();
    }
}

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        DataContext = new SettingsViewModel();
        InitializeComponent();
    }
}

public sealed class DashboardViewModel
{
    public AppState App => AppState.Instance;
}
