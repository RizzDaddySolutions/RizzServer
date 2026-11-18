using System.Windows;

namespace RizzServer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppState.Instance.Initialize();
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppState.Instance.Shutdown();
        base.OnExit(e);
    }
}
