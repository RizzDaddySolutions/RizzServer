using System.Text.Json;
using RizzServer.Core;

namespace RizzServer.App;

public enum ThemeMode { System, Light, Dark }

/// <summary>Where a window was last, in screen pixels (position) and DIPs (size).</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Per-user UI preferences (theme, window size/position, pane sizes). Stored beside the project
/// file but separate from it, because they describe this machine's screen, not the project.
/// </summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static UiSettings Instance { get; private set; } = new();

    /// <summary>Overridable for tests.</summary>
    public static string FilePath { get; set; } = Path.Combine(ConfigStore.DataDirectory, "ui-settings.json");

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Dictionary<string, WindowPlacement> Windows { get; set; } = new();
    public double TreeWidth { get; set; } = 260;
    public double LogHeight { get; set; } = 190;
    public bool LogVisible { get; set; } = true;

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Instance = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Instance = new UiSettings(); // unreadable preferences must never stop the app starting
        }
        return Instance;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best-effort.
        }
    }
}
