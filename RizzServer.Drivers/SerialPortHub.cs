using System.IO.Ports;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// Shares one physical serial port between the devices of a channel (multi-drop buses).
/// Reference counted: the port opens with the first device and closes with the last.
/// </summary>
internal static class SerialPortHub
{
    private sealed class Entry
    {
        public required SerialPort Port { get; init; }
        public required string Signature { get; init; }
        public required bool Exclusive { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public int Refs { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opens (or joins) the port named in the config. <paramref name="exclusive"/> drivers read an
    /// unsolicited stream, so only one device may use the port.
    /// </summary>
    public static SerialPortLease Acquire(ConnectionConfig config, bool exclusive, Action<SerialPort>? configure = null)
    {
        var name = config.Setting("PortName", "COM1");
        var signature = string.Join('/', config.Setting("BaudRate", "9600"), config.Setting("DataBits", "8"),
            config.Setting("Parity", "None"), config.Setting("StopBits", "One"));

        lock (Gate)
        {
            if (Entries.TryGetValue(name, out var existing))
            {
                if (exclusive || existing.Exclusive)
                    throw new InvalidOperationException(
                        $"{name} is already used by another device on this channel; this driver supports one device per port.");
                if (existing.Signature != signature)
                    throw new InvalidOperationException($"{name} is already open with different serial settings ({existing.Signature}).");
                existing.Refs++;
                return new SerialPortLease(existing.Port, existing.Lock, () => Release(name));
            }

            var port = new SerialPort(name, config.SettingInt("BaudRate", 9600),
                Enum.Parse<Parity>(config.Setting("Parity", "None")), config.SettingInt("DataBits", 8),
                Enum.Parse<StopBits>(config.Setting("StopBits", "One")));
            configure?.Invoke(port);
            port.Open(); // throws if the port is missing/busy; nothing registered yet

            var entry = new Entry { Port = port, Signature = signature, Exclusive = exclusive, Refs = 1 };
            Entries[name] = entry;
            return new SerialPortLease(port, entry.Lock, () => Release(name));
        }
    }

    private static void Release(string name)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(name, out var entry)) return;
            if (--entry.Refs > 0) return;
            Entries.Remove(name);
            try { entry.Port.Dispose(); } catch { /* already gone */ }
        }
    }
}

/// <summary>A device's claim on a shared port. <see cref="Lock"/> serialises request/response exchanges on the bus.</summary>
internal sealed class SerialPortLease : IDisposable
{
    private Action? _release;

    public SerialPortLease(SerialPort port, SemaphoreSlim @lock, Action release)
    {
        Port = port;
        Lock = @lock;
        _release = release;
    }

    public SerialPort Port { get; }
    public SemaphoreSlim Lock { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
