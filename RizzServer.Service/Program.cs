using System.Reflection;
using System.Text.Json.Serialization;
using RizzServer.Core;
using RizzServer.Server;
using RizzServer.Service;
using RizzServer.Service.Api;

// ---- project-as-code commands (validate, diff, export, apply, schema) ----
if (Cli.TryRun(args, Console.Out, Console.Error) is { } exitCode) return exitCode;

// --config <file> runs the project from a file (JSON or YAML) and reloads it when the file changes.
string? projectFile = null;
var cfgIndex = Array.FindIndex(args, a => a == "--config");
if (cfgIndex >= 0)
{
    projectFile = cfgIndex + 1 < args.Length ? args[cfgIndex + 1] : throw new ArgumentException("--config needs a file name.");
    if (!File.Exists(projectFile)) throw new FileNotFoundException($"Project file '{projectFile}' not found.");
}

var settings = ServiceSettings.LoadOrCreate(out var firstRunKey);
var audit = new AuditLog();
var apiFailures = new FailureTracker(settings.MaxFailedAuth);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "RizzServer");
builder.Host.UseSystemd();

// --urls / ASPNETCORE_URLS win; otherwise service.json decides.
var urlsOverridden = !string.IsNullOrEmpty(builder.Configuration["urls"]);
if (!urlsOverridden) builder.WebHost.UseUrls([.. settings.Urls]);

var logBuffer = new LogBuffer();
builder.Logging.AddProvider(logBuffer);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);   // no per-request lines on the console

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(audit);
builder.Services.AddSingleton(apiFailures);
builder.Services.AddSingleton(logBuffer);
builder.Services.AddSingleton(sp =>
{
    if (projectFile is null) return new GatewayRuntime(ConfigStore.Load(), sp.GetRequiredService<ILoggerFactory>());
    var fromFile = ConfigStore.LoadFile(projectFile);
    var problems = ConfigValidator.Validate(fromFile);
    if (problems.Count > 0) throw new InvalidOperationException($"{projectFile} is not valid: " + string.Join("; ", problems));
    return new GatewayRuntime(fromFile, sp.GetRequiredService<ILoggerFactory>()) { SourceFile = Path.GetFullPath(projectFile) };
});
if (projectFile is not null)
    builder.Services.AddHostedService(sp => new ConfigFileWatcher(sp.GetRequiredService<GatewayRuntime>(), projectFile, sp.GetRequiredService<ILogger<ConfigFileWatcher>>()));
builder.Services.AddSingleton<ConfigEditor>();
builder.Services.AddHostedService<GatewayHostedService>();

if (!settings.HasCredentials && !settings.IsLoopbackOnly())
    throw new InvalidOperationException(
        "The API has no key but is configured to listen on a non-loopback address. Create one: rizzserver-service keys create admin Admin (or set RIZZSERVER_API_KEY).");
if (!settings.IsLoopbackOnly() && !settings.UsesHttps && !settings.AllowInsecureRemote)
    throw new InvalidOperationException(
        "The API would listen on a non-loopback address over plain HTTP, which sends the API key in clear text. Use an https:// URL, or set AllowInsecureRemote in service.json if a reverse proxy terminates TLS.");

if (settings.UsesHttps && !urlsOverridden)
{
    var certificate = TlsCertificate.Load(settings, out var selfSigned);
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = certificate));
    if (selfSigned) Console.WriteLine($"HTTPS uses a self-signed certificate ({TlsCertificate.DefaultPath}, valid until {certificate.NotAfter:yyyy-MM-dd}). Clients must trust it.");
}
var app = builder.Build();

app.Logger.LogInformation("RizzServer service {Version}; data folder {Folder}", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3), ConfigStore.DataDirectory);
if (firstRunKey is not null)
{
    // The only time the key is visible: only its hash is stored.
    Console.WriteLine("First run: created an admin API key. Save it now, it cannot be shown again:");
    Console.WriteLine("  " + firstRunKey);
    Console.WriteLine("Create more keys with: rizzserver-service keys create <name> <Viewer|Operator|Admin>");
}
if (!settings.HasCredentials)
    app.Logger.LogWarning("API authentication is OFF (no API keys). Only acceptable on a loopback address.");
if (!settings.IsLoopbackOnly() && settings.AllowInsecureRemote)
    app.Logger.LogWarning("AllowInsecureRemote is on: make sure TLS is terminated in front of this service.");

var runtimeForAudit = app.Services.GetRequiredService<GatewayRuntime>();
runtimeForAudit.Engine.Audit = audit.Write;
audit.Write("service-start", "system", "service", ConfigStore.DataDirectory);
app.UseMiddleware<ApiAuthMiddleware>();
app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new { name = "RizzServer", api = "/api/v1", openapi = "/openapi/v1.json", health = "/health" }));
app.MapGet("/health", (GatewayRuntime rt) => Results.Ok(new { status = "ok", running = rt.IsRunning }));

var api = app.MapGroup("/api/v1");
RuntimeApi.Map(api);
ConfigApi.Map(api);
ProjectApi.Map(api);
OutputsApi.Map(api);
SecurityApi.Map(api);
CommissioningApi.Map(api);

await app.RunAsync();
return 0;
