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

        _port = new SerialPort(Config.Setting("PortName", "COM1"), Config.SettingInt("BaudRate", 9600),
            Enum.Parse<Parity>(Config.Setting("Parity", "None")), Config.SettingInt("DataBits", 8),
            Enum.Parse<StopBits>(Config.Setting("StopBits", "One")))
        {
            Encoding = Encoding.ASCII,
        };
        _port.DataReceived += OnData;
        _port.Open();
        return Task.CompletedTask;
    }

    protected override Task DisconnectAsync()
    {
        if (_port is not null) _port.DataReceived -= OnData;
        _port?.Dispose();
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
        string[]? fields = null;
        foreach (var tag in Tags)
        {
            var addr = tag.Address.Trim();
            string? value = null;
            if (addr.Equals("line", StringComparison.OrdinalIgnoreCase)) value = line;
            else if (addr.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, addr[3..]);
                if (m.Success) value = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
            }
            else if (int.TryParse(addr, out var index))
            {
                fields ??= line.Split(_separator);
                if (index >= 0 && index < fields.Length) value = fields[index].Trim();
            }
            if (value is not null) Publish(tag, value);
        }
    }

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var port = _port;
        if (port is not { IsOpen: true }) throw new InvalidOperationException("Not connected.");
        port.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) + _terminator);
        return Task.CompletedTask;
    }
}
