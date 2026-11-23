using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RizzServer.App;

/// <summary>
/// Remembers a window's size, position and maximised state across runs. The saved position is
/// ignored when it is no longer on any screen (monitor unplugged, resolution changed).
/// </summary>
public static class WindowPlacementService
{
    public static void Attach(Window window, string key, bool restorePosition = true)
    {
        var settings = UiSettings.Instance;
        var normal = settings.Windows.GetValueOrDefault(key);

        if (normal is { Width: > 0, Height: > 0 })
        {
            window.Width = Math.Max(window.MinWidth, normal.Width);
            window.Height = Math.Max(window.MinHeight, normal.Height);
        }

        // Track the last non-maximised bounds so a maximised window still remembers its restore size.
        var last = normal ?? new WindowPlacement();
        var save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        save.Tick += (_, _) => { save.Stop(); Persist(); };

        void Capture()
        {
            if (window.WindowState == WindowState.Normal)
            {
                last.X = window.Position.X;
                last.Y = window.Position.Y;
                last.Width = window.ClientSize.Width;
                last.Height = window.ClientSize.Height;
            }
            last.Maximized = window.WindowState == WindowState.Maximized;
        }

        void Persist()
        {
            Capture();
            settings.Windows[key] = Clone(last);
            settings.Save();
        }

        window.Opened += (_, _) =>
        {
            if (restorePosition && normal is { Width: > 0 } && IsOnScreen(window, normal))
                window.Position = new PixelPoint(normal.X, normal.Y);
            if (normal is { Maximized: true }) window.WindowState = WindowState.Maximized;

            // Only start listening once the initial placement is done, so restoring doesn't re-save.
            window.PositionChanged += (_, _) => { Capture(); save.Stop(); save.Start(); };
            window.Resized += (_, _) => { Capture(); save.Stop(); save.Start(); };
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) { Capture(); save.Stop(); save.Start(); }
            };
        };

        window.Closing += (_, _) => { save.Stop(); Persist(); };
    }

    /// <summary>True when the title-bar area of the saved rectangle is visible on some screen.</summary>
    public static bool IsOnScreen(Window window, WindowPlacement p)
    {
        var screens = window.Screens?.All;
        if (screens is null || screens.Count == 0) return true; // can't tell; let the OS place it
        var grab = new PixelPoint(p.X + 60, p.Y + 12);
        return screens.Any(s => s.WorkingArea.Contains(grab));
    }

    private static WindowPlacement Clone(WindowPlacement p) =>
        new() { X = p.X, Y = p.Y, Width = p.Width, Height = p.Height, Maximized = p.Maximized };
}

