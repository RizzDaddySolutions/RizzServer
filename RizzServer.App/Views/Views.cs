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
            node.Open(grid.SelectedItem as ChannelConfig);
    }
}

public partial class ChannelView : UserControl
{
    public ChannelView() => InitializeComponent();

    private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ChannelNode node && sender is DataGrid grid)
            node.Open(grid.SelectedItem as DeviceConfig);
    }
}

public partial class DeviceView : UserControl
{
    public DeviceView() => InitializeComponent();
}

public partial class ServerView : UserControl
{
    public ServerView() => InitializeComponent();
}

public partial class QuickClientView : UserControl
{
    public QuickClientView() => InitializeComponent();
}

public partial class OutputsView : UserControl
{
    public OutputsView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is OutputsNode node) node.OpenCommand.Execute(null);
    }
}

public partial class OutputView : UserControl
{
    public OutputView() => InitializeComponent();
}

public partial class TemplatesView : UserControl
{
    public TemplatesView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TemplatesNode node) node.OpenCommand.Execute(null);
    }
}

public partial class TemplateView : UserControl
{
    public TemplateView() => InitializeComponent();
}
