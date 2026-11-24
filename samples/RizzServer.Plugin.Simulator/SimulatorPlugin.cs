using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RizzServer.Sdk;

namespace RizzServer.Plugin.Simulator;

/// <summary>
/// A driver that needs no hardware: it makes up values. Useful for trying RizzServer, for demos, and as the smallest
/// complete example of a plugin. Tag addresses:
/// <list type="bullet">
/// <item><c>sine:period_s[:amplitude[:offset]]</c> – a sine wave</item>
/// <item><c>ramp:period_s[:min[:max]]</c> – a sawtooth</item>
/// <item><c>random:min:max</c> – a new random value on every scan</item>
/// <item><c>counter</c> – 0, 1, 2, … one step per scan</item>
/// <item><c>const:value</c> – a fixed value</item>
/// <item><c>mem:name[:initial]</c> – a memory cell that can be written (use ReadWrite tags)</item>
/// </list>
/// </summary>
public sealed class SimulatorPlugin : IDriverPlugin
{
    public PluginDescriptor Describe() => new()
    {
        Id = "rizz.simulator",
        DisplayName = "Simulator (no hardware)",
        Version = "1.0.0",
        AddressHint = "sine:period_s[:amp[:offset]], ramp:period_s[:min[:max]], random:min:max, counter, const:value, mem:name[:initial] (writable).",
        Settings =
        [
            new SettingInfo("Seed", "Random seed (0 = different every run)", "0", SettingLevel.Device),
            new SettingInfo("DropoutAfterSeconds", "Simulate a connection loss after N seconds (0 = never)", "0", SettingLevel.Device),
        ],
    };

    public PluginDriver Create(DriverContext context) => new SimulatorDriver(context);
}

internal sealed class SimulatorDriver(DriverContext context) : PluginDriver(context)
{
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<string, object> _memory = new(StringComparer.OrdinalIgnoreCase);
    private Random _random = new();
    private long _scans;
    private bool _dropped;

    protected override Task ConnectAsync(CancellationToken ct)
    {
        var seed = Context.SettingInt("Seed", 0);
        _random = seed == 0 ? new Random() : new Random(seed);
        // The clock keeps running after a reconnect (a dropout is simulated only once per run, see PollAsync, so no
        // endless loop); stopping it here froze every sine and ramp tag at t = 0.
        _clock.Restart();
        return Task.CompletedTask;
    }

    protected override Task PollAsync(CancellationToken ct)
    {
        var dropout = Context.SettingInt("DropoutAfterSeconds", 0);
        if (dropout > 0 && !_dropped && _clock.Elapsed.TotalSeconds >= dropout)
        {
            _dropped = true;
            throw new IOException("Simulated connection loss.");
        }

        var t = _clock.Elapsed.TotalSeconds;
        _scans++;
        foreach (var tag in Context.Tags)
        {
            try { Publish(tag, Evaluate(tag, t)); }
            catch (FormatException ex)
            {
                Context.Log.LogWarning("{Tag}: {Error}", tag.Name, ex.Message);
                Publish(tag, null, SdkQuality.Bad);
            }
        }
        return Task.CompletedTask;
    }

    private object Evaluate(SdkTag tag, double t)
    {
        var p = tag.Address.Split(':', StringSplitOptions.TrimEntries);
        double D(int i, double fallback) => p.Length > i && double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        switch (p[0].ToLowerInvariant())
        {
            case "sine":
            {
                var period = Math.Max(0.001, D(1, 10));
                return D(3, 0) + D(2, 1) * Math.Sin(2 * Math.PI * t / period);
            }
            case "ramp":
            {
                var period = Math.Max(0.001, D(1, 10));
                var min = D(2, 0);
                return min + (D(3, 100) - min) * ((t % period) / period);
            }
            case "random":
                return D(1, 0) + _random.NextDouble() * (D(2, 1) - D(1, 0));
            case "counter":
                return _scans;
            case "const":
                return p.Length > 1 ? p[1] : "0";
            case "mem":
                if (p.Length < 2) throw new FormatException("mem needs a name: mem:name[:initial]");
                if (!_memory.TryGetValue(p[1], out var value)) _memory[p[1]] = value = p.Length > 2 ? p[2] : "0";
                return value;
            default:
                throw new FormatException($"Unknown address '{tag.Address}'. See the address hint.");
        }
    }

    protected override Task WriteAsync(SdkTag tag, object value, CancellationToken ct)
    {
        var p = tag.Address.Split(':', StringSplitOptions.TrimEntries);
        if (!p[0].Equals("mem", StringComparison.OrdinalIgnoreCase) || p.Length < 2)
            throw new NotSupportedException("Only mem:name tags can be written.");
        _memory[p[1]] = value;
        Publish(tag, value);
        return Task.CompletedTask;
    }
}
