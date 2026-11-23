using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// RS-232 request/response driver for instruments that only answer when asked.
/// Tag address: <c>command[|parse[|writeTemplate]]</c>
/// <list type="bullet">
/// <item><c>command</c> – text sent each poll (escapes: \r \n \t \xHH); the command terminator is appended.</item>
/// <item><c>parse</c> – empty = whole reply, a number = field index, or <c>re:pattern</c> (first capture group).</item>
/// <item><c>writeTemplate</c> – sent on write with <c>{0}</c> replaced by the value; the reply is ignored.</item>
/// </list>
/// </summary>
public sealed class Rs232PollDriver : DriverBase
{
    private SerialPort? _port;
    private SerialPortLease? _lease;
    private int _timeoutMs = 1000;

    private SemaphoreSlim Io => _lease?.Lock ?? throw new IOException("Serial port closed.");
    private string _replyTerminator = "\r\n";
    private string _commandTerminator = "\r";
    private string _separator = ",";
    private int _gapMs = 50;

    public Rs232PollDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    private static string Unescape(string s) =>
        Regex.Replace(s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t"),
            @"\\x([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static (string Command, string Parse, string? Write) Split(string address)
    {
        var parts = address.Split('|', 3);
        return (parts[0], parts.Length > 1 ? parts[1].Trim() : "", parts.Length > 2 ? parts[2] : null);
    }

    protected override Task ConnectAsync(CancellationToken ct)
    {
        _replyTerminator = Unescape(Config.Setting("Terminator", "\\r\\n"));
        _commandTerminator = Unescape(Config.Setting("CommandTerminator", "\\r"));
        _separator = Config.Setting("Separator", ",");
        _gapMs = Math.Max(0, Config.SettingInt("InterCommandDelayMs", 50));

        _timeoutMs = Config.SettingInt("TimeoutMs", 1000);
        // Devices on the channel share the bus; exchanges are serialised by the lease's lock.
        _lease = SerialPortHub.Acquire(Config, exclusive: false, p => p.Encoding = Encoding.Latin1);
        _port = _lease.Port;
        return Task.CompletedTask;
    }

    protected override Task DisconnectAsync()
    {
        _lease?.Dispose();
        _lease = null;
        _port = null;
        return Task.CompletedTask;
    }

    protected override async Task PollAsync(CancellationToken ct)
    {
        var io = Io;
        await io.WaitAsync(ct);
        try
        {
            var first = true;
            foreach (var tag in Tags)
            {
                ct.ThrowIfCancellationRequested();
                if (!first && _gapMs > 0) await Task.Delay(_gapMs, ct);
                first = false;

                var (command, parse, _) = Split(tag.Address);
                if (command.Length == 0) { Publish(tag, null, TagQuality.Bad); continue; }

                try
                {
                    var reply = await Task.Run(() => Exchange(command), ct);
                    var value = Extract(reply, parse);
                    if (value is null)
                    {
                        Log.LogWarning("[{Conn}] {Tag}: reply '{Reply}' did not match '{Parse}'", Config.Name, tag.Name, reply, parse);
                        Publish(tag, null, TagQuality.Bad);
                    }
                    else Publish(tag, value);
                }
                catch (TimeoutException)
                {
                    Log.LogWarning("[{Conn}] {Tag}: no reply to '{Cmd}'", Config.Name, tag.Name, command);
                    Publish(tag, null, TagQuality.Bad);
                }
            }
        }
        finally { io.Release(); }
    }

    /// <summary>Sends one command and reads one terminated reply. Throws TimeoutException if none arrives.</summary>
    private string Exchange(string command)
    {
        var port = _port is { IsOpen: true } p ? p : throw new IOException("Serial port closed.");
        port.ReadTimeout = _timeoutMs;   // per-device timeout on a shared port (set under the bus lock)
        port.WriteTimeout = _timeoutMs;
        port.DiscardInBuffer();
        port.Write(Unescape(command) + _commandTerminator);
        return port.ReadTo(_replyTerminator);
    }

    private string? Extract(string reply, string parse)
    {
        if (parse.Length == 0) return reply.Trim();
        if (parse.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(reply, parse[3..]);
            return !m.Success ? null : m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }
        if (int.TryParse(parse, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var fields = reply.Split(_separator);
            return index < fields.Length ? fields[index].Trim() : null;
        }
        return null;
    }

    public override async Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var (_, _, template) = Split(tag.Address);
        if (string.IsNullOrEmpty(template))
            throw new InvalidOperationException("Tag has no write template (address: command|parse|writeTemplate).");

        var text = value is bool b ? (b ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var payload = Unescape(template.Replace("{0}", text)) + _commandTerminator;

        var io = Io;
        await io.WaitAsync(ct);
        try
        {
            var port = _port is { IsOpen: true } p ? p : throw new InvalidOperationException("Not connected.");
            port.Write(payload);
        }
        finally { io.Release(); }
    }
}
