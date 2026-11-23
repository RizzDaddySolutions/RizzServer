using Avalonia;
using Avalonia.Styling;

namespace RizzServer.App;

public static class ThemeService
{
    /// <summary>Applies the mode to the running app. System follows the operating system live.</summary>
    public static void Apply(ThemeMode mode)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = mode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
    }

    /// <summary>Applies and remembers the mode.</summary>
    public static void Set(ThemeMode mode)
    {
        Apply(mode);
        UiSettings.Instance.Theme = mode;
        UiSettings.Instance.Save();
    }
}
