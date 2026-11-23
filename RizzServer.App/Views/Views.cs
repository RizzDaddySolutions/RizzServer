using Avalonia.Controls;
using Avalonia.Input;
using RizzServer.App.ViewModels;
using RizzServer.Core;

namespace RizzServer.App.Views;

public partial class ProjectView : UserControl
{
    public ProjectView() => InitializeComponent();
}

public partial class ConnectivityView : UserControl
{
    public ConnectivityView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ConnectivityNode node && sender is DataGrid grid)
            node.Open(grid.SelectedItem as ConnectionConfig);
    }
}

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();
}

public partial class ServerView : UserControl
{
    public ServerView() => InitializeComponent();
}

public partial class QuickClientView : UserControl
{
    public QuickClientView() => InitializeComponent();
}
