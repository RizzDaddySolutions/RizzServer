using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// Plain RS-232 text device. Splits incoming data into lines; each tag picks a field
/// (index), the whole line ("line") or a regex capture ("re:pattern").
/// </summary>
public sealed class SerialDriver : DriverBase
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private SerialPort? _port;
    private SerialPortLease? _lease;
    private string _terminator = "\n";
    private string _separator = ",";

    public SerialDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    private static string Unescape(string s) =>
        s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");

    protected override Task ConnectAsync(CancellationToken ct)
    {
        _terminator = Unescape(Config.Setting("Terminator", "\\r\\n"));
        _separator = Config.Setting("Separator", ",");
        _buffer.Clear();

        // Receive-only stream: one device per port.
        _lease = SerialPortHub.Acquire(Config, exclusive: true, p => p.Encoding = Encoding.ASCII);
        _port = _lease.Port;
        _port.DataReceived += OnData;
        return Task.CompletedTask;
    }

    protected override Task DisconnectAsync()
    {
        if (_port is not null) _port.DataReceived -= OnData;
        _lease?.Dispose();
        _lease = null;
        _port = null;
        return Task.CompletedTask;
    }

    protected override Task PollAsync(CancellationToken ct)
    {
        if (_port is not { IsOpen: true }) throw new IOException("Serial port closed.");
        return Task.CompletedTask;
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var port = _port;
            if (port is null) return;
            var chunk = port.ReadExisting();
            List<string> lines = new();
            lock (_lock)
            {
                _buffer.Append(chunk);
                var text = _buffer.ToString();
                int idx;
                while ((idx = text.IndexOf(_terminator, StringComparison.Ordinal)) >= 0)
                {
                    lines.Add(text[..idx]);
                    text = text[(idx + _terminator.Length)..];
                }
                _buffer.Clear().Append(text);
                if (_buffer.Length > 65536) _buffer.Clear(); // runaway guard
            }
            foreach (var line in lines) HandleLine(line);
        }
        catch (Exception ex)
        {
            Log.LogWarning("[{Conn}] serial read: {Error}", Config.Name, ex.Message);
        }
    }

    private void HandleLine(string line)
    {
        foreach (var tag in Tags)
            if (TextExtract.Extract(line, tag.Address, _separator) is { } value) Publish(tag, value);
    }

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var port = _port;
        if (port is not { IsOpen: true }) throw new InvalidOperationException("Not connected.");
        port.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) + _terminator);
        return Task.CompletedTask;
    }
}
