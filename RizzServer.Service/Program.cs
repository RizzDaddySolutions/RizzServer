using System.Reflection;
using System.Text.Json.Serialization;
using RizzServer.Core;
using RizzServer.Server;
using RizzServer.Service;
using RizzServer.Service.Api;

// ---- tiny CLI for key management; runs before the host is built ----
if (args.Contains("--show-api-key") || args.Contains("--new-api-key"))
{
    var s = ServiceSettings.LoadOrCreate(out _);
    if (args.Contains("--new-api-key"))
    {
        s.ApiKey = ServiceSettings.NewApiKey();
        s.Save();
    }
    Console.WriteLine(s.ApiKey);
    return;
}

var settings = ServiceSettings.LoadOrCreate(out var createdSettings);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "RizzServer");
builder.Host.UseSystemd();

// --urls / ASPNETCORE_URLS win; otherwise service.json decides.
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls([.. settings.Urls]);

var logBuffer = new LogBuffer();
builder.Logging.AddProvider(logBuffer);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);   // no per-request lines on the console

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(logBuffer);
builder.Services.AddSingleton(sp => new GatewayRuntime(ConfigStore.Load(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<ConfigEditor>();
builder.Services.AddHostedService<GatewayHostedService>();

if (string.IsNullOrEmpty(settings.ApiKey) && !settings.IsLoopbackOnly())
    throw new InvalidOperationException(
        "The API has no key but is configured to listen on a non-loopback address. Set ApiKey in service.json or RIZZSERVER_API_KEY.");

var app = builder.Build();

app.Logger.LogInformation("RizzServer service {Version}; data folder {Folder}", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3), ConfigStore.DataDirectory);
if (createdSettings)
    app.Logger.LogWarning("First run: an API key was generated in {File}. Print it with: rizzserver-service --show-api-key", ServiceSettings.FilePath);
if (string.IsNullOrEmpty(settings.ApiKey))
    app.Logger.LogWarning("API authentication is OFF (no ApiKey). Only acceptable on a loopback address.");
if (!settings.IsLoopbackOnly() && settings.Urls.Any(u => u.StartsWith("http:", StringComparison.OrdinalIgnoreCase)))
    app.Logger.LogWarning("The API listens on a non-loopback address over plain HTTP: the API key travels unencrypted. Use HTTPS or a reverse proxy.");

app.UseMiddleware<ApiKeyMiddleware>();
app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new { name = "RizzServer", api = "/api/v1", openapi = "/openapi/v1.json", health = "/health" }));
app.MapGet("/health", (GatewayRuntime rt) => Results.Ok(new { status = "ok", running = rt.IsRunning }));

var api = app.MapGroup("/api/v1");
RuntimeApi.Map(api);
ConfigApi.Map(api);

await app.RunAsync();
