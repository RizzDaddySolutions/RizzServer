using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RizzServer.Core;

namespace RizzServer.Service;

/// <summary>One credential for the REST API. Only a hash of the key is stored: a lost key is replaced, not recovered.</summary>
public sealed class ApiKeyEntry
{
    public string Name { get; set; } = "";
    /// <summary>Viewer: read. Operator: also write tag values and run commissioning tests. Admin: everything.</summary>
    public UserRole Role { get; set; } = UserRole.Admin;
    public string Hash { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

/// <summary>
/// Settings of the headless host itself (not the project): where the API listens, its credentials, TLS, and whether the
/// runtime starts with the process. Stored as <c>service.json</c> next to the project file.
/// </summary>
public sealed class ServiceSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Loopback by default. A public address needs HTTPS (see <see cref="AllowInsecureRemote"/>).</summary>
    public List<string> Urls { get; set; } = ["http://127.0.0.1:5080"];

    /// <summary>API credentials, sent as <c>Authorization: Bearer &lt;key&gt;</c> or <c>X-Api-Key</c>. No keys = authentication off (loopback only).</summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = new();

    /// <summary>Old single plain-text key from earlier versions. Read once, converted to a hashed key named "default", then cleared.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ApiKey { get; set; } = "";

    /// <summary>Start the runtime when the process starts.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>A PFX file for HTTPS. Empty = a self-signed certificate is created in the data folder when an https URL is used.</summary>
    public string CertificatePath { get; set; } = "";
    /// <summary>Password of <see cref="CertificatePath"/>; stored encrypted (see SecretStore) and may be a ${env:NAME} reference.</summary>
    public string CertificatePassword { get; set; } = "";

    /// <summary>Allow plain http:// on a non-loopback address. Off by default: the key would travel in clear text.</summary>
    public bool AllowInsecureRemote { get; set; }

    /// <summary>Wrong keys from one address before it is blocked for five minutes.</summary>
    public int MaxFailedAuth { get; set; } = 10;

    public static string FilePath => Path.Combine(ConfigStore.DataDirectory, "service.json");

    /// <summary>Keys given by the environment (RIZZSERVER_API_KEY): admin, never written to disk.</summary>
    [JsonIgnore] public string? EnvironmentKey { get; private set; }

    [JsonIgnore] public bool HasCredentials => ApiKeys.Count > 0 || !string.IsNullOrEmpty(EnvironmentKey);

    /// <summary>Loads the file. On the first run it creates one admin key and returns it in <paramref name="newKey"/> (shown once).</summary>
    public static ServiceSettings LoadOrCreate(out string? newKey)
    {
        newKey = null;
        ServiceSettings settings;
        if (File.Exists(FilePath))
        {
            settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(FilePath), Options) ?? new();
            var dirty = false;
            if (settings.ApiKey.Length > 0)
            {
                // Migrate the old plain key: same value keeps working, but it is no longer stored.
                settings.ApiKeys.Add(new ApiKeyEntry { Name = "default", Role = UserRole.Admin, Hash = ApiKeyEntry.HashOf(settings.ApiKey) });
                settings.ApiKey = "";
                dirty = true;
            }
            if (settings.CertificatePassword.Length > 0 && !SecretStore.IsProtected(settings.CertificatePassword) && !SecretStore.IsReference(settings.CertificatePassword))
                dirty = true;   // saved encrypted below
            if (dirty) settings.Save();
            if (settings.CertificatePassword.Length > 0) settings.CertificatePassword = SecretStore.Unprotect(settings.CertificatePassword);
        }
        else
        {
            settings = new ServiceSettings();
            newKey = settings.AddKey("default", UserRole.Admin);
            settings.Save();
        }

        // Environment overrides are handy for containers.
        if (Environment.GetEnvironmentVariable("RIZZSERVER_API_KEY") is { Length: > 0 } key) settings.EnvironmentKey = key;
        if (Environment.GetEnvironmentVariable("RIZZSERVER_API_URLS") is { Length: > 0 } urls)
            settings.Urls = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (Environment.GetEnvironmentVariable("RIZZSERVER_AUTOSTART") is { Length: > 0 } auto && bool.TryParse(auto, out var b))
            settings.AutoStart = b;
        return settings;
    }

    /// <summary>Creates a key and stores its hash. The plain key is returned once and cannot be shown again.</summary>
    public string AddKey(string name, UserRole role)
    {
        if (ApiKeys.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A key named '{name}' already exists.");
        var key = NewApiKey();
        ApiKeys.Add(new ApiKeyEntry { Name = name, Role = role, Hash = ApiKeyEntry.HashOf(key) });
        return key;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigStore.DataDirectory);
        var copy = new ServiceSettings
        {
            Urls = Urls, ApiKeys = ApiKeys, AutoStart = AutoStart, CertificatePath = CertificatePath,
            AllowInsecureRemote = AllowInsecureRemote, MaxFailedAuth = MaxFailedAuth,
            CertificatePassword = SecretStore.Protect(CertificatePassword),
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, Options));
        // The file holds credentials: keep it owner-only where the OS supports it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static string NewApiKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public bool IsLoopbackOnly() => Urls.All(u =>
        Uri.TryCreate(u.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host is "localhost"));

    public ServiceExposure Exposure() => new(HasCredentials, IsLoopbackOnly(), UsesHttps, CertificatePath.Length == 0,
        ApiKeys.Count(k => k.Role == UserRole.Admin), AllowInsecureRemote);

    public bool UsesHttps => Urls.Any(u => u.StartsWith("https:", StringComparison.OrdinalIgnoreCase));
}
