namespace RizzServer.App;

/// <summary>
/// File pickers and message boxes for view models. The main window supplies the implementations,
/// which keeps the view models free of UI types (and testable).
/// </summary>
public static class FileService
{
    /// <summary>Title → chosen path, or null if cancelled.</summary>
    public static Func<string, Task<string?>>? PickOpen { get; set; }

    /// <summary>(Title, suggested file name) → chosen path, or null if cancelled.</summary>
    public static Func<string, string, Task<string?>>? PickSave { get; set; }

    /// <summary>(Title, text) → shown until the user dismisses it.</summary>
    public static Func<string, string, Task>? Message { get; set; }
}
