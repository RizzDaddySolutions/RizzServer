using System.Security.Cryptography;
using System.Text.Json;
using RizzServer.Core;

namespace RizzServer.Service;

/// <summary>
/// Settings of the headless host itself (not the project): where the API listens, its key, and whether the
/// runtime starts with the process. Stored as <c>service.json</c> next to the project file.
/// </summary>
public sealed class ServiceSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Loopback by default. Changing this to a public address needs HTTPS (see README).</summary>
    public List<string> Urls { get; set; } = ["http://127.0.0.1:5080"];

    /// <summary>Clients send it as <c>Authorization: Bearer &lt;key&gt;</c> or <c>X-Api-Key</c>. Empty disables auth (loopback only).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Start the runtime when the process starts.</summary>
    public bool AutoStart { get; set; } = true;

    public static string FilePath => Path.Combine(ConfigStore.DataDirectory, "service.json");

    /// <summary>Loads the file, creating it (with a fresh random API key) on first run.</summary>
    public static ServiceSettings LoadOrCreate(out bool created)
    {
        created = false;
        ServiceSettings settings;
        if (File.Exists(FilePath))
            settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(FilePath), Options) ?? new();
        else
        {
            settings = new ServiceSettings { ApiKey = NewApiKey() };
            settings.Save();
            created = true;
        }

        // Environment overrides are handy for containers.
        if (Environment.GetEnvironmentVariable("RIZZSERVER_API_KEY") is { Length: > 0 } key) settings.ApiKey = key;
        if (Environment.GetEnvironmentVariable("RIZZSERVER_API_URLS") is { Length: > 0 } urls)
            settings.Urls = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (Environment.GetEnvironmentVariable("RIZZSERVER_AUTOSTART") is { Length: > 0 } auto && bool.TryParse(auto, out var b))
            settings.AutoStart = b;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigStore.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        // The file holds a secret: keep it owner-only where the OS supports it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static string NewApiKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public bool IsLoopbackOnly() => Urls.All(u =>
        Uri.TryCreate(u.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host is "localhost"));
}
