using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RizzServer.Core;

/// <summary>
/// Keeps passwords and keys out of the project file in clear text.
/// <list type="bullet">
/// <item><c>enc:v1:…</c> values are AES-256-GCM encrypted with a key kept beside the project (<c>secret.key</c>) or
/// supplied in <c>RIZZSERVER_SECRET_KEY</c> (base64, 32 bytes). Plain values found in an older file are encrypted on the next save.</item>
/// <item><c>${env:NAME}</c> and <c>${file:/path}</c> are references: the value is read from the environment or a file
/// when the runtime starts and is never written to the project. This is how a Git-tracked project carries no secrets.</item>
/// </list>
/// </summary>
public static class SecretStore
{
    public const string Prefix = "enc:v1:";
    private static readonly Regex Reference = new(@"\$\{(?<kind>env|file):(?<arg>[^}]+)\}", RegexOptions.Compiled);
    private static byte[]? _key;
    private static readonly object KeyLock = new();

    public static string KeyPath => Path.Combine(ConfigStore.DataDirectory, "secret.key");

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);
    public static bool IsReference(string value) => Reference.IsMatch(value);

    private static byte[] Key()
    {
        lock (KeyLock)
        {
            if (_key is not null) return _key;
            if (Environment.GetEnvironmentVariable("RIZZSERVER_SECRET_KEY") is { Length: > 0 } fromEnv)
            {
                var bytes = Convert.FromBase64String(fromEnv);
                if (bytes.Length != 32) throw new InvalidOperationException("RIZZSERVER_SECRET_KEY must be 32 bytes, base64 encoded.");
                return _key = bytes;
            }

            Directory.CreateDirectory(ConfigStore.DataDirectory);
            if (File.Exists(KeyPath)) return _key = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());

            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(KeyPath, Convert.ToBase64String(fresh));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return _key = fresh;
        }
    }

    /// <summary>For tests: forget the cached key so a different data folder / environment is picked up.</summary>
    public static void ResetKeyCache() { lock (KeyLock) _key = null; }

    public static string Protect(string plain)
    {
        if (plain.Length == 0 || IsProtected(plain) || IsReference(plain)) return plain;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Decrypts a protected value. Plain values and references are returned unchanged.</summary>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            var all = Convert.FromBase64String(value[Prefix.Length..]);
            var nonce = all[..12];
            var tag = all[12..28];
            var cipher = all[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("A stored secret could not be decrypted: the project was saved with a different secret key (secret.key / RIZZSERVER_SECRET_KEY).", ex);
        }
    }

    /// <summary>The value a driver or output should actually use: decrypted, with references resolved.</summary>
    public static string Resolve(string value)
    {
        value = Unprotect(value);
        return Reference.Replace(value, m =>
        {
            var arg = m.Groups["arg"].Value.Trim();
            if (m.Groups["kind"].Value == "env")
                return Environment.GetEnvironmentVariable(arg)
                       ?? throw new InvalidOperationException($"Environment variable '{arg}' (referenced as a secret) is not set.");
            return File.Exists(arg)
                ? File.ReadAllText(arg).Trim()
                : throw new InvalidOperationException($"Secret file '{arg}' does not exist.");
        });
    }

    /// <summary>Resolves every value of a settings dictionary (decrypt + references). Used when a driver is configured.</summary>
    public static Dictionary<string, string> ExpandReferences(Dictionary<string, string> settings)
    {
        var result = new Dictionary<string, string>(settings.Count);
        foreach (var (k, v) in settings)
        {
            try { result[k] = IsProtected(v) || IsReference(v) ? Resolve(v) : v; }
            catch (InvalidOperationException) { result[k] = v; }   // surfaced when the driver fails to connect, not while building the model
        }
        return result;
    }

    // ---- whole-project passes ----

    /// <summary>Encrypts every secret setting of the (already cloned) project in place.</summary>
    public static void ProtectProject(GatewayConfig config) => Walk(config, Protect);

    /// <summary>Decrypts every secret setting of a freshly loaded project in place (references stay as written).</summary>
    public static void UnprotectProject(GatewayConfig config) => Walk(config, Unprotect);

    private static void Walk(GatewayConfig config, Func<string, string> transform)
    {
        foreach (var channel in config.Channels)
        {
            var info = DriverCatalog.For(channel);
            Apply(channel.Settings, info.ChannelSettings, transform);
            foreach (var d in channel.Devices) Apply(d.Settings, info.DeviceSettings, transform);
        }
        foreach (var n in config.Northbound)
            Apply(n.Settings, NorthboundCatalog.For(n.Kind), transform);
        if (config.Redundancy.PeerApiKey.Length > 0) config.Redundancy.PeerApiKey = transform(config.Redundancy.PeerApiKey);
    }

    private static void Apply(Dictionary<string, string> settings, IEnumerable<SettingDescriptor> descriptors, Func<string, string> transform)
    {
        foreach (var d in descriptors.Where(d => d.Secret))
            if (settings.TryGetValue(d.Key, out var v) && v.Length > 0) settings[d.Key] = transform(v);
    }
}
