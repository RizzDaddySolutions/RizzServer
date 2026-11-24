using System.IO.Ports;
using System.Text;
using RizzServer.Drivers;

namespace RizzServer.App.Designer;

/// <summary>A live stream of raw bytes to analyse. Implemented for a serial port; tests use a fake.</summary>
public interface IByteSource : IDisposable
{
    /// <summary>Raised on a background thread with each chunk as it arrived and when.</summary>
    event Action<byte[], DateTime>? DataReceived;

    /// <summary>Writes bytes to the device (a terminal command). Throws <see cref="InvalidOperationException"/> when the source isn't open.</summary>
    void Send(byte[] data);

    /// <summary>Opens the source. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't.</summary>
    void Start();
}

public sealed class SerialByteSource(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits) : IByteSource
{
    private SerialPort? _port;

    public event Action<byte[], DateTime>? DataReceived;

    public void Start()
    {
        var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
        port.DataReceived += (_, _) =>
        {
            try
            {
                var n = port.BytesToRead;
                if (n <= 0) return;
                var buffer = new byte[n];
                n = port.Read(buffer, 0, n);
                DataReceived?.Invoke(buffer.AsSpan(0, n).ToArray(), DateTime.Now);
            }
            catch (Exception) { /* port closed while reading */ }
        };
        port.Open();
        _port = port;
    }

    public void Send(byte[] data)
    {
        if (_port is not { IsOpen: true } port) throw new InvalidOperationException("The port is not open.");
        port.Write(data, 0, data.Length);
    }

    public void Dispose()
    {
        try { _port?.Dispose(); } catch { /* already gone */ }
        _port = null;
    }
}

/// <summary>Reads a capture saved as raw bytes or as hex text (a dump, a log, something pasted from a datasheet).</summary>
public static class CaptureFile
{
    public static bool TryLoad(byte[] file, out byte[] bytes)
    {
        // Hex text first: if the file is printable and yields hex bytes, that is what the person meant.
        if (file.All(b => b is 9 or 10 or 13 or (>= 32 and < 127)))
        {
            var text = Encoding.ASCII.GetString(file);
            if (HexText.TryParse(text, out var parsed))
            {
                var tokens = text.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Length;
                if (parsed.Length * 2 >= tokens) { bytes = parsed; return true; }   // mostly hex, not prose
            }
        }

        bytes = file;
        return file.Length > 0;
    }
}
