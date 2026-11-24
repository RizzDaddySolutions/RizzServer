using RizzServer.Core;

namespace RizzServer.Service;

/// <summary>
/// Command-line tools for treating a project as code. They never start the runtime:
/// <code>
/// rizzserver-service validate project.yaml
/// rizzserver-service diff project.yaml [other.yaml]     (default: against the stored project)
/// rizzserver-service export project.yaml [--with-secrets]
/// rizzserver-service apply project.yaml
/// rizzserver-service schema
/// rizzserver-service keys list | create &lt;name&gt; [Viewer|Operator|Admin] | revoke &lt;name&gt;
/// rizzserver-service audit tail [count] | verify
/// rizzserver-service security-report
/// rizzserver-service harden
/// </code>
/// Exit code 0 = ok, 1 = problems found, 2 = bad usage.
/// </summary>
public static class Cli
{
    public static readonly string[] Commands = ["validate", "diff", "export", "apply", "schema", "keys", "audit", "security-report", "harden"];

    public static int? TryRun(string[] args, TextWriter @out, TextWriter err)
    {
        if (args.Length == 0 || !Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase)) return null;
        var rest = args.Skip(1).ToList();
        var flags = rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "schema":
                    @out.WriteLine(ConfigStore.Schema());
                    return 0;

                case "keys": return Keys(files, @out, err);
                case "audit": return Audit(files, @out, err);

                case "security-report":
                {
                    var settings = ServiceSettings.LoadOrCreate(out _);
                    var findings = SecurityReport.Check(ConfigStore.Load(), settings);
                    foreach (var f in findings) @out.WriteLine($"[{f.Severity}] {f.Title}\n        {f.Detail}\n        Fix: {f.Fix}");
                    @out.WriteLine(findings.Count == 0 ? "No findings." : $"{findings.Count} finding(s).");
                    return findings.Any(f => f.Severity == "High") ? 1 : 0;
                }

                case "harden": return Harden(@out);

                case "validate":
                    if (files.Count != 1) return Usage(err, "validate <file>");
                    return Validate(Load(files[0]), files[0], @out, err);

                case "diff":
                {
                    if (files.Count is < 1 or > 2) return Usage(err, "diff <file> [other-file]");
                    var left = files.Count == 2 ? Load(files[0]) : ConfigStore.Load();
                    var right = Load(files[^1]);
                    var changes = ConfigDiff.Compare(left, right);
                    foreach (var c in changes) @out.WriteLine(c);
                    @out.WriteLine(changes.Count == 0 ? "No differences." : $"{changes.Count} difference(s).");
                    return changes.Count == 0 ? 0 : 1;
                }

                case "export":
                {
                    if (files.Count != 1) return Usage(err, "export <file.json|.yaml> [--with-secrets]");
                    ConfigStore.SaveFile(files[0], ConfigStore.Load(), flags.Contains("--with-secrets"), out var vars);
                    @out.WriteLine($"Wrote {files[0]}.");
                    if (vars.Count > 0)
                    {
                        @out.WriteLine("Secrets are referenced, not stored. Set these environment variables where the project runs:");
                        foreach (var v in vars) @out.WriteLine("  " + v);
                    }
                    return 0;
                }

                case "apply":
                {
                    if (files.Count != 1) return Usage(err, "apply <file>");
                    var incoming = Load(files[0]);
                    var errors = ConfigValidator.Validate(incoming);
                    if (errors.Count > 0) { Report(errors, err); return 1; }

                    var changes = ConfigDiff.Compare(ConfigStore.Load(), incoming);
                    foreach (var c in changes) @out.WriteLine(c);
                    if (flags.Contains("--dry-run")) { @out.WriteLine($"Dry run: {changes.Count} change(s) not applied."); return 0; }
                    ConfigStore.Save(incoming);
                    @out.WriteLine($"Applied {changes.Count} change(s) to {ConfigStore.ConfigPath}. Restart the service to use them (or use POST /api/v1/config/apply?restart=true on a running one).");
                    return 0;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            err.WriteLine(ex.Message);
            return 1;
        }
        return null;
    }

    private static int Keys(List<string> a, TextWriter @out, TextWriter err)
    {
        var settings = ServiceSettings.LoadOrCreate(out var first);
        if (first is not null) @out.WriteLine($"(first run: created admin key 'default': {first})");
        switch (a.FirstOrDefault()?.ToLowerInvariant())
        {
            case "list":
                foreach (var k in settings.ApiKeys) @out.WriteLine($"{k.Name,-20} {k.Role,-9} created {k.Created:yyyy-MM-dd}");
                if (settings.ApiKeys.Count == 0) @out.WriteLine("No keys: the API is open (loopback only).");
                return 0;
            case "create" when a.Count is 2 or 3:
                if (a.Count == 3 && !Enum.TryParse<UserRole>(a[2], true, out _)) return Usage(err, "keys create <name> [Viewer|Operator|Admin]");
                try
                {
                    var role = a.Count == 3 ? Enum.Parse<UserRole>(a[2], true) : UserRole.Viewer;
                    var key = settings.AddKey(a[1], role);
                    settings.Save();
                    new AuditLog().Write("key-created", "cli", a[1], role.ToString());
                    @out.WriteLine($"Key '{a[1]}' ({role}) created. Save it now, it cannot be shown again:\n  {key}");
                    return 0;
                }
                catch (InvalidOperationException ex) { err.WriteLine(ex.Message); return 1; }
            case "revoke" when a.Count == 2:
                var entry = settings.ApiKeys.FirstOrDefault(k => string.Equals(k.Name, a[1], StringComparison.OrdinalIgnoreCase));
                if (entry is null) { err.WriteLine($"No key named '{a[1]}'."); return 1; }
                if (entry.Role == UserRole.Admin && settings.ApiKeys.Count(k => k.Role == UserRole.Admin) == 1)
                { err.WriteLine("This is the only admin key; create another first."); return 1; }
                settings.ApiKeys.Remove(entry);
                settings.Save();
                new AuditLog().Write("key-revoked", "cli", a[1], "");
                @out.WriteLine($"Key '{a[1]}' revoked.");
                return 0;
            default: return Usage(err, "keys list | create <name> [Viewer|Operator|Admin] | revoke <name>");
        }
    }

    private static int Audit(List<string> a, TextWriter @out, TextWriter err)
    {
        var log = new AuditLog();
        switch (a.FirstOrDefault()?.ToLowerInvariant())
        {
            case "tail":
                foreach (var e in log.Tail(a.Count > 1 && int.TryParse(a[1], out var n) ? n : 50))
                    @out.WriteLine($"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Action,-14} {e.User,-14} {e.Target}  {e.Detail}");
                return 0;
            case "verify":
                var (lines, bad, problem) = log.Verify();
                if (bad is null) { @out.WriteLine($"Audit log intact ({lines} entries)."); return 0; }
                err.WriteLine($"Audit log broken at line {bad}: {problem}");
                return 1;
            default: return Usage(err, "audit tail [count] | verify");
        }
    }

    /// <summary>Closes the common holes in the stored project: no anonymous access, no unencrypted OPC UA, no blind certificate trust.</summary>
    private static int Harden(TextWriter @out)
    {
        var config = ConfigStore.Load();
        var s = config.Server;
        var changes = new List<string>();
        if (s.Users.Count == 0)
        {
            var password = ServiceSettings.NewApiKey()[..20];
            s.Users.Add(new UserAccount { Username = "admin", Role = UserRole.Admin, PasswordHash = PasswordHasher.Hash(password) });
            changes.Add($"Created OPC UA user 'admin' (Admin) with password {password}   <- save it now, it cannot be shown again");
        }
        if (s.AllowAnonymous) { s.AllowAnonymous = false; changes.Add("OPC UA anonymous access turned off"); }
        if (s.AllowNoSecurity) { s.AllowNoSecurity = false; changes.Add("OPC UA 'None' security mode turned off (clients must sign & encrypt)"); }
        if (s.AutoAcceptCertificates) { s.AutoAcceptCertificates = false; changes.Add("Client certificates are no longer auto-accepted: copy trusted certificates into the 'trusted' folder of the pki directory"); }
        if (changes.Count == 0) { @out.WriteLine("Nothing to change: the project is already hardened."); return 0; }

        ConfigStore.Save(config);
        new AuditLog().Write("harden", "cli", "project", string.Join("; ", changes.Select(c => c.Split("   <-")[0])));
        foreach (var c in changes) @out.WriteLine("- " + c);
        @out.WriteLine("Saved. Restart the service for the OPC UA changes to take effect.");
        return 0;
    }

    private static GatewayConfig Load(string path) =>
        File.Exists(path) ? ConfigStore.LoadFile(path) : throw new FileNotFoundException($"File '{path}' not found.");

    private static int Validate(GatewayConfig config, string path, TextWriter @out, TextWriter err)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0) { Report(errors, err); return 1; }
        @out.WriteLine($"{path}: valid - {config.Channels.Count} channel(s), {config.Channels.Sum(c => c.Devices.Count)} device(s), " +
                       $"{config.Channels.Sum(c => c.Devices.Sum(d => d.Tags.Count))} tag(s), {config.Templates.Count} template(s), {config.Northbound.Count} output(s).");
        return 0;
    }

    private static void Report(IEnumerable<string> errors, TextWriter err)
    {
        foreach (var e in errors) err.WriteLine("error: " + e);
    }

    private static int Usage(TextWriter err, string usage)
    {
        err.WriteLine("usage: rizzserver-service " + usage);
        return 2;
    }
}

/// <summary>Reloads the project when its file changes (GitOps: the file in the repository is the truth).</summary>
public sealed class ConfigFileWatcher(RizzServer.Server.GatewayRuntime runtime, string path, ILogger<ConfigFileWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var full = Path.GetFullPath(path);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        var changed = new SemaphoreSlim(0);
        watcher.Changed += (_, _) => changed.Release();
        watcher.Created += (_, _) => changed.Release();
        watcher.Renamed += (_, _) => changed.Release();   // editors and git replace the file

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await changed.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            // Editors write in several steps; wait for the dust to settle and drain the extra events.
            await Task.Delay(500, stoppingToken);
            while (changed.CurrentCount > 0) await changed.WaitAsync(0, stoppingToken);

            try { await ReloadAsync(full); }
            catch (Exception ex) { log.LogError("Reloading {File} failed: {Error}. The previous project keeps running.", full, ex.Message); }
        }
    }

    private async Task ReloadAsync(string full)
    {
        var incoming = ConfigStore.LoadFile(full);
        var errors = ConfigValidator.Validate(incoming);
        if (errors.Count > 0)
        {
            log.LogError("{File} is not valid, keeping the running project: {Errors}", full, string.Join("; ", errors));
            return;
        }
        var changes = ConfigDiff.Compare(runtime.Config, incoming);
        if (changes.Count == 0) return;

        log.LogInformation("{File} changed ({Count} difference(s)); reloading.", full, changes.Count);
        foreach (var c in changes.Take(20)) log.LogInformation("  {Change}", c);
        await runtime.ReloadAsync(incoming);
    }
}
