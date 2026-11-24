using System.IO.Ports;
using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

public sealed record TestBody(string Channel, string Device, int? TimeoutMs);
public sealed record ProbeAddress(string Address, TagDataType? DataType);
public sealed record ReadBody(string Channel, string Device, List<ProbeAddress> Addresses, int? TimeoutMs);
public sealed record ModbusScanBody(string Channel, string Device, string Table, int Start, int Count, int? TimeoutMs);

/// <summary>Commissioning: per-device health, connection tests, trial reads and a Modbus register scan.</summary>
public static class CommissioningApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Commissioning");

        g.MapGet("/diagnostics", (GatewayRuntime rt) => Results.Ok(rt.Engine.GetDiagnostics()))
         .WithSummary("Per device: state, uptime, reconnects, poll count/errors/duration, update rate, good and bad tags.");

        g.MapGet("/diagnostics/{channel}/{device}/events", (string channel, string device, GatewayRuntime rt) =>
            Results.Ok(rt.Engine.GetEvents(channel, device)))
         .WithSummary("The last events of a device: connects, faults and their reasons.");

        g.MapGet("/commissioning/ports", () => Results.Ok(SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)))
         .WithSummary("Serial ports of the machine the service runs on.");

        g.MapPost("/commissioning/test", async (TestBody body, GatewayRuntime rt, CancellationToken ct) =>
        {
            if (Resolve(rt, body.Channel, body.Device) is not var (channel, device)) return NotFound($"Device '{body.Channel}.{body.Device}'");
            return Results.Ok(await Commissioning.TestAsync(rt.Engine.Factory, channel, device, Timeout(body.TimeoutMs), ct));
        })
         .WithSummary("Connect to a configured device on its own and read all its tags once. Works whether or not the runtime is running.");

        g.MapPost("/commissioning/read", async (ReadBody body, GatewayRuntime rt, CancellationToken ct) =>
        {
            if (Resolve(rt, body.Channel, body.Device) is not var (channel, device)) return NotFound($"Device '{body.Channel}.{body.Device}'");
            if (body.Addresses is not { Count: > 0 and <= 200 }) return Json(400, new ErrorBody("Give between 1 and 200 addresses."));
            var list = body.Addresses.Select(a => (a.Address, a.DataType ?? TagDataType.Int16));
            return Results.Ok(await Commissioning.ReadAsync(rt.Engine.Factory, channel, device, list, Timeout(body.TimeoutMs), ct));
        })
         .WithSummary("Read addresses that aren't tags yet, to find out what a device has and how to decode it.");

        g.MapPost("/commissioning/modbus-scan", async (ModbusScanBody body, GatewayRuntime rt, CancellationToken ct) =>
        {
            if (Resolve(rt, body.Channel, body.Device) is not var (channel, device)) return NotFound($"Device '{body.Channel}.{body.Device}'");
            if (channel.DriverType is not (DriverType.ModbusTcp or DriverType.ModbusRtu))
                return Json(400, new ErrorBody("This channel is not a Modbus channel."));
            var table = body.Table.ToUpperInvariant();
            if (table is not ("HR" or "IR" or "C" or "DI")) return Json(400, new ErrorBody("Table must be HR, IR, C or DI."));
            if (body.Start < 0 || body.Count is < 1 or > 200 || body.Start + body.Count > 65536)
                return Json(400, new ErrorBody("Scan 1 to 200 addresses within 0..65535 per request."));

            var type = table is "C" or "DI" ? TagDataType.Boolean : TagDataType.UInt16;
            var addresses = Enumerable.Range(body.Start, body.Count).Select(n => ($"{table}{n}", type));
            var result = await Commissioning.ReadAsync(rt.Engine.Factory, channel, device, addresses, Timeout(body.TimeoutMs ?? 15000), ct);

            // Collapse consecutive readable addresses into ranges.
            var readable = result.Tags.Select((t, i) => (Address: body.Start + i, t.Quality, t.Value)).Where(x => x.Quality == "Good").ToList();
            var ranges = new List<object>();
            for (var i = 0; i < readable.Count;)
            {
                var j = i;
                while (j + 1 < readable.Count && readable[j + 1].Address == readable[j].Address + 1) j++;
                ranges.Add(new { from = readable[i].Address, to = readable[j].Address, values = readable.Skip(i).Take(j - i + 1).Select(x => x.Value) });
                i = j + 1;
            }
            return Results.Ok(new { result.Connected, result.Message, table, readable = readable.Count, scanned = body.Count, ranges });
        })
         .WithSummary("Find which Modbus addresses answer: scans up to 200 addresses of a table (HR, IR, C, DI) and returns the readable ranges with their values.");
    }

    private static TimeSpan Timeout(int? ms) => TimeSpan.FromMilliseconds(Math.Clamp(ms ?? 5000, 500, 60000));

    private static (ChannelConfig, DeviceConfig)? Resolve(GatewayRuntime rt, string channel, string device) =>
        FindChannel(rt.Config, channel) is { } ch && FindDevice(ch, device) is { } d ? (ch, d) : null;
}
