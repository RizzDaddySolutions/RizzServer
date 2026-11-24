using System.Text.Json;
using System.Threading.Channels;
using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

/// <summary>Runtime control, live data, device status, drivers and the event log.</summary>
public static class RuntimeApi
{
    public static TagBody ToBody(TagRuntime t) => new(
        t.NodePath, t.ChannelName, t.DeviceName, t.Config.Name, t.Value, t.Quality.ToString(),
        t.Timestamp, t.Config.DataType.ToString(), t.Config.Access.ToString());

    public static StatusBody Status(GatewayRuntime rt)
    {
        var devices = rt.Engine.Connections;
        return new StatusBody(
            rt.IsRunning, rt.StartedAt, rt.EndpointUrl, rt.LastError,
            rt.Config.Channels.Count, rt.Config.Channels.Sum(c => c.Devices.Count),
            rt.Config.Channels.Sum(c => c.Devices.Sum(d => d.Tags.Count)),
            rt.IsRunning ? devices.Count(d => d.State == ConnectionState.Connected) : 0,
            rt.IsRunning ? devices.Count(d => d.State == ConnectionState.Faulted) : 0);
    }

    public static void Map(RouteGroupBuilder api)
    {
        var rt = api.MapGroup("/runtime").WithTags("Runtime");
        rt.MapGet("", (GatewayRuntime runtime) => Results.Ok(Status(runtime)));

        rt.MapPost("/start", async (GatewayRuntime runtime) => await Run(runtime, runtime.StartAsync))
          .WithSummary("Start the drivers and the OPC UA endpoint.");
        rt.MapPost("/stop", async (GatewayRuntime runtime) => await Run(runtime, runtime.StopAsync));
        rt.MapPost("/restart", async (GatewayRuntime runtime) => await Run(runtime, runtime.RestartAsync))
          .WithSummary("Stop and start again, e.g. after changing the configuration.");

        var data = api.MapGroup("").WithTags("Live data");

        data.MapGet("/devices/status", (GatewayRuntime runtime) => Results.Ok(
            Running(runtime, runtime.Engine.Connections).Select(c =>
                new DeviceStatusBody(c.Config.ChannelName, c.Config.DeviceName, c.State.ToString(), c.Message))));

        data.MapGet("/tags", (GatewayRuntime runtime, string? channel, string? device, string? quality) =>
        {
            var tags = Running(runtime, runtime.Engine.Tags);
            if (channel is not null) tags = tags.Where(t => Eq(t.ChannelName, channel));
            if (device is not null) tags = tags.Where(t => Eq(t.DeviceName, device));
            if (quality is not null) tags = tags.Where(t => Eq(t.Quality.ToString(), quality));
            return Results.Ok(tags.Select(ToBody));
        })
        .WithSummary("Current value, quality and timestamp of every running tag (filters: channel, device, quality).");

        data.MapGet("/tags/{*path}", (string path, GatewayRuntime runtime) =>
            Find(runtime, path) is { } t ? Results.Ok(ToBody(t)) : NotFound($"Tag '{path}'"));

        data.MapPut("/tags/{*path}", async (string path, WriteBody body, GatewayRuntime runtime) =>
        {
            if (!runtime.IsRunning) return Json(409, new ErrorBody("The runtime is not running."));
            if (Find(runtime, path) is not { } tag) return NotFound($"Tag '{path}'");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runtime.Engine.WriteAsync(tag, ToObject(body.Value), cts.Token);
                return Results.Ok(ToBody(tag));
            }
            catch (InvalidOperationException ex) when (tag.Config.Access != TagAccess.ReadWrite)
            {
                return Json(403, new ErrorBody(ex.Message));
            }
            catch (InvalidOperationException ex) { return Json(409, new ErrorBody(ex.Message)); }
            catch (ArgumentException ex) { return Json(400, new ErrorBody(ex.Message)); }
            catch (Exception ex) { return Json(502, new ErrorBody("The device did not accept the write: " + ex.Message)); }
        })
        .WithSummary("Write a value ({\"value\": 12.5}) through the owning driver to the device.");

        data.MapGet("/stream", Stream).WithSummary("Server-Sent Events: one 'tag' event per value change (filters: channel, device; snapshot=true sends current values first).");

        var misc = api.MapGroup("").WithTags("System");

        misc.MapGet("/drivers", () => Results.Ok(DriverCatalog.Available.Select(d => new
        {
            type = d.Type.ToString(),
            pluginId = d.PluginId,
            source = d.Source,
            name = d.DisplayName,
            addressHint = d.AddressHint,
            maxDevices = d.MaxDevices,
            channelSettings = d.ChannelSettings.Select(Setting),
            deviceSettings = d.DeviceSettings.Select(Setting),
        })))
        .WithSummary("Every driver with the settings its channels and devices accept (use it to build valid configurations).");

        misc.MapGet("/log", (LogBuffer log, int? limit, string? level) =>
        {
            var lines = log.Snapshot().AsEnumerable();
            if (level is not null) lines = lines.Where(l => Eq(l.Level, level));
            return Results.Ok(lines.TakeLast(Math.Clamp(limit ?? 200, 1, 1000)));
        });
    }

    private static object Setting(SettingDescriptor s) => new { key = s.Key, label = s.Label, defaultValue = s.Default, options = s.Options, secret = s.Secret };

    private static async Task<IResult> Run(GatewayRuntime runtime, Func<Task> action)
    {
        try
        {
            await action();
            return Results.Ok(Status(runtime));
        }
        catch (Exception ex)
        {
            return Json(500, new ErrorBody("The runtime could not be started: " + ex.Message));
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine keeps its last tag list after a stop (the UI shows it greyed out); the API reports nothing then.</summary>
    private static IEnumerable<T> Running<T>(GatewayRuntime rt, IEnumerable<T> items) => rt.IsRunning ? items : [];

    private static TagRuntime? Find(GatewayRuntime rt, string path) =>
        Running(rt, rt.Engine.Tags).FirstOrDefault(t => t.NodePath == path)
        ?? Running(rt, rt.Engine.Tags).FirstOrDefault(t => Eq(t.NodePath, path));

    private static object ToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException("value must be a string, number or boolean."),
    };

    private static async Task Stream(HttpContext http, GatewayRuntime runtime, string? channel, string? device, bool? snapshot)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";   // don't let nginx buffer the stream

        var queue = Channel.CreateBounded<TagBody>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        bool Wanted(TagRuntime t) => (channel is null || Eq(t.ChannelName, channel)) && (device is null || Eq(t.DeviceName, device));
        void OnUpdate(TagRuntime t) { if (Wanted(t)) queue.Writer.TryWrite(ToBody(t)); }

        runtime.Engine.TagUpdated += OnUpdate;
        try
        {
            var ct = http.RequestAborted;
            await response.WriteAsync(": connected\n\n", ct);
            if (snapshot == true)
                foreach (var t in Running(runtime, runtime.Engine.Tags).Where(Wanted))
                    await response.WriteAsync($"event: tag\ndata: {JsonSerializer.Serialize(ToBody(t), JsonOptions)}\n\n", ct);
            await response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var item = await queue.Reader.ReadAsync(wait.Token);
                    await response.WriteAsync($"event: tag\ndata: {JsonSerializer.Serialize(item, JsonOptions)}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.WriteAsync(": ping\n\n", ct);   // keep proxies from closing an idle stream
                }
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
        finally { runtime.Engine.TagUpdated -= OnUpdate; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
