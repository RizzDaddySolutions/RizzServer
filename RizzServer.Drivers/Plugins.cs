using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RizzServer.Core;
using RizzServer.Sdk;

namespace RizzServer.Drivers;

/// <summary>What happened to one plugin folder at startup.</summary>
public sealed record PluginStatus(string Id, string Name, string Version, string Source, string State, string? Error, string Sha256);

/// <summary>Runs a plugin driver inside the server's connect/poll/reconnect loop.</summary>
internal sealed class PluginAdapter : DriverBase
{
    private readonly PluginDriver _plugin;
    private readonly Dictionary<string, SdkTag> _sdkTags;
    private readonly Dictionary<string, TagConfig> _byName;

    public PluginAdapter(ConnectionConfig config, ILogger log, PluginDriver plugin, Dictionary<string, SdkTag> sdkTags)
        : base(config, log)
    {
        _plugin = plugin;
        _sdkTags = sdkTags;
        _byName = config.Tags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        plugin.Attach((tag, value, quality) =>
        {
            if (_byName.TryGetValue(tag.Name, out var cfg))
                Publish(cfg, value, quality switch { SdkQuality.Good => TagQuality.Good, SdkQuality.Uncertain => TagQuality.Uncertain, _ => TagQuality.Bad });
        });
    }

    protected override Task ConnectAsync(CancellationToken ct) => _plugin.HostConnectAsync(ct);
    protected override Task PollAsync(CancellationToken ct) => _plugin.HostPollAsync(ct);
    protected override Task DisconnectAsync() => _plugin.HostDisconnectAsync();

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default) =>
        _sdkTags.TryGetValue(tag.Name, out var sdk)
            ? _plugin.HostWriteAsync(sdk, value, ct)
            : throw new InvalidOperationException($"Tag {tag.Name} is not known to the plugin.");
}

/// <summary>
/// Finds, loads and registers driver plugins. Each plugin lives in its own folder and its own <see cref="AssemblyLoadContext"/>
/// (its private dependencies don't clash with the server's or other plugins'); only the SDK assembly is shared.
/// <para><b>Plugins are code that runs inside the server with its full rights.</b> Only install plugins you trust. Each loaded
/// assembly's SHA-256 is logged and listed by the API. Set <c>RIZZSERVER_PLUGINS=off</c> to load none, or list the hashes you
/// accept in <c>plugins/trusted.txt</c> (one per line) to load only those.</para>
/// </summary>
public static class PluginLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IDriverPlugin> Plugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PluginStatus> Results = [];
    private static bool _loaded;

    /// <summary>Folders searched for plugins: next to the program and in the data folder.</summary>
    public static IReadOnlyList<string> SearchFolders { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, "plugins"),
        Path.Combine(ConfigStore.DataDirectory, "plugins"),
    ];

    public static IReadOnlyList<PluginStatus> Status { get { lock (Gate) return [.. Results]; } }

    public static bool TryGet(string id, out IDriverPlugin plugin) { lock (Gate) return Plugins.TryGetValue(id, out plugin!); }

    /// <summary>Loads every plugin once. Safe to call repeatedly; failures are recorded per plugin and never stop the server.</summary>
    public static void EnsureLoaded(ILogger? log = null, params string[] extraFolders)
    {
        lock (Gate)
        {
            if (_loaded && extraFolders.Length == 0) return;
            _loaded = true;

            if (string.Equals(Environment.GetEnvironmentVariable("RIZZSERVER_PLUGINS"), "off", StringComparison.OrdinalIgnoreCase))
            {
                log?.LogInformation("Plugins are disabled (RIZZSERVER_PLUGINS=off).");
                return;
            }

            foreach (var root in SearchFolders.Concat(extraFolders).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                var trusted = ReadTrustList(root);
                // plugins/<name>/<name>.dll — one folder per plugin; loose DLLs directly in plugins/ work too.
                var folders = Directory.GetDirectories(root).Concat([root]);
                foreach (var folder in folders)
                    foreach (var dll in Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
                        TryLoad(dll, trusted, log);
            }
        }
    }

    private static HashSet<string>? ReadTrustList(string root)
    {
        var file = Path.Combine(root, "trusted.txt");
        if (!File.Exists(file)) return null;
        return File.ReadAllLines(file).Select(l => l.Split('#')[0].Trim().ToLowerInvariant()).Where(l => l.Length > 0).ToHashSet();
    }

    private static void TryLoad(string dll, HashSet<string>? trusted, ILogger? log)
    {
        // Only assemblies that reference the SDK can be plugins; skip dependencies sitting in the same folder.
        AssemblyName name;
        try { name = AssemblyName.GetAssemblyName(dll); }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException) { return; }   // native or unrelated DLL
        if (name.Name is "RizzServer.Sdk") return;

        string hash;
        try { hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(); }
        catch (IOException) { return; }

        if (!ReferencesSdk(dll)) return;

        var source = Path.GetRelativePath(AppContext.BaseDirectory, dll);
        if (trusted is not null && !trusted.Contains(hash))
        {
            Results.Add(new PluginStatus(name.Name ?? "?", name.Name ?? "?", name.Version?.ToString() ?? "", source, "Rejected", "its SHA-256 is not in trusted.txt", hash));
            log?.LogWarning("Plugin {File} not loaded: SHA-256 {Hash} is not in trusted.txt", dll, hash);
            return;
        }

        try
        {
            var context = new PluginLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
            var contract = assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "RizzServer.Sdk")?.Version;
            var host = typeof(IDriverPlugin).Assembly.GetName().Version;
            if (contract is not null && host is not null && contract.Major > host.Major)
            {
                Results.Add(new PluginStatus(name.Name ?? "?", name.Name ?? "?", name.Version?.ToString() ?? "", source, "Rejected", $"built for SDK {contract}, this server has {host}", hash));
                return;
            }

            var types = SafeTypes(assembly).Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDriverPlugin).IsAssignableFrom(t)).ToList();
            if (types.Count == 0) return;

            foreach (var type in types)
            {
                var plugin = (IDriverPlugin)Activator.CreateInstance(type)!;
                var d = plugin.Describe();
                if (!System.Text.RegularExpressions.Regex.IsMatch(d.Id, "^[a-z0-9][a-z0-9.-]*$"))
                    throw new InvalidOperationException($"The plugin id '{d.Id}' must be lower case letters, digits, dots and dashes.");

                DriverCatalog.Register(Describe(d, source));
                Plugins[d.Id] = plugin;
                Results.Add(new PluginStatus(d.Id, d.DisplayName, d.Version, source, "Loaded", null, hash));
                log?.LogInformation("Plugin '{Id}' ({Name} {Version}) loaded from {File}, SHA-256 {Hash}", d.Id, d.DisplayName, d.Version, source, hash);
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            Results.Add(new PluginStatus(name.Name ?? "?", name.Name ?? "?", name.Version?.ToString() ?? "", source, "Failed", inner.Message, hash));
            log?.LogError("Plugin {File} failed to load: {Error}", dll, inner.Message);
        }
    }

    private static bool ReferencesSdk(string dll)
    {
        try
        {
            using var stream = File.OpenRead(dll);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return false;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == "RizzServer.Sdk");
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException) { return false; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static DriverInfo Describe(PluginDescriptor d, string source) =>
        new(DriverType.Plugin, d.DisplayName, d.AddressHint,
            [.. d.Settings.Select(s => new SettingDescriptor(s.Key, s.Label, s.Default,
                s.Level == SettingLevel.Channel ? SettingScope.Channel : SettingScope.Device, s.Options, s.Secret))])
        { MaxDevices = d.MaxDevices, PluginId = d.Id, Source = source };

    /// <summary>Creates the driver for one device of a plugin channel.</summary>
    internal static IDriver Create(ConnectionConfig config, ILogger log)
    {
        if (config.PluginId is null || !TryGet(config.PluginId, out var plugin))
            throw new NotSupportedException($"The plugin '{config.PluginId}' is not installed (see the plugins folders: {string.Join(", ", SearchFolders)}).");

        var sdkTags = config.Tags.Select(t => new SdkTag(t.Name, t.Address, Enum.Parse<SdkDataType>(t.DataType.ToString()), t.Access == TagAccess.ReadWrite))
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var context = new DriverContext
        {
            Name = config.Name,
            Settings = SecretStore.ExpandReferences(config.Settings),
            Tags = [.. sdkTags.Values],
            PollInterval = TimeSpan.FromMilliseconds(Math.Max(50, config.PollIntervalMs)),
            Log = log,
        };
        return new PluginAdapter(config, log, plugin.Create(context), sdkTags);
    }

    /// <summary>Each plugin resolves its own dependencies from its folder; the SDK and the framework come from the server.</summary>
    private sealed class PluginLoadContext(string path) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "RizzServer.Sdk" or "Microsoft.Extensions.Logging.Abstractions") return null;   // shared with the host
            var found = _resolver.ResolveAssemblyToPath(name);
            return found is null ? null : LoadFromAssemblyPath(found);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var found = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return found is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(found);
        }
    }
}
