using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RizzServer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppState.Instance.Initialize();
            desktop.MainWindow = new Views.MainWindow { DataContext = new ViewModels.MainViewModel() };
            desktop.Exit += (_, _) => AppState.Instance.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
