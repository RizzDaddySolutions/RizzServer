using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RizzServer.Core;

namespace RizzServer.App.Views;

public sealed record ComponentInfo(string Name, string Version);

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<DriverInfo> Drivers { get; } =
        DriverCatalog.Available.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public int DriverCount => Drivers.Count;

    public IReadOnlyList<ComponentInfo> Components { get; } =
    [
        new(".NET runtime", RuntimeInformation.FrameworkDescription.Replace(".NET ", "")),
        new("Avalonia UI", VersionOf(typeof(Avalonia.Application))),
        new("OPC Foundation UA .NET Standard", VersionOf(typeof(Opc.Ua.NodeId))),
        new("Operating system", RuntimeInformation.OSDescription),
    ];

    public static string Version => VersionOf(typeof(AboutWindow));

    private static string VersionOf(Type type)
    {
        var v = type.Assembly.GetName().Version ?? new Version(0, 1);
        return $"{v.Major}.{v.Minor}" + (v.Build > 0 ? $".{v.Build}" : "");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
