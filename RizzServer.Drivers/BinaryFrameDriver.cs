using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// RS-232 binary protocol: frames are synchronised on a header, optionally validated by a checksum,
/// and each tag decodes a field at a byte offset (address <c>offset:type[:scale]</c>, e.g. <c>4:i16:0.1</c>).
/// An optional request is sent every poll for devices that must be triggered. Tags with a write template
/// (<c>offset:type[:scale]|AA55 05 {v} {ck}</c>) can be written.
/// </summary>
public sealed class BinaryFrameDriver : DriverBase
{
    private readonly object _lock = new();
    private SerialPort? _port;
    private SerialPortLease? _lease;
    private FrameAssembler _assembler = null!;
    private Dictionary<TagConfig, BinaryFieldSpec> _specs = new();
    private bool _bigEndian = true;
    private byte[] _request = [];
    private int _badFrames;
    private ChecksumKind _checksum;
    private int _checksumStart;
    private readonly object _txLock = new();

    public BinaryFrameDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    protected override Task ConnectAsync(CancellationToken ct)
    {
        _bigEndian = !Config.Setting("ByteOrder", "BigEndian").Equals("LittleEndian", StringComparison.OrdinalIgnoreCase);
        _request = FrameAssembler.ParseHex(Config.Setting("Request"));

        _specs = new Dictionary<TagConfig, BinaryFieldSpec>();
        foreach (var tag in Tags)
        {
            try { _specs[tag] = BinaryFieldSpec.Parse(BinaryFieldSpec.SplitAddress(tag.Address).Field); }
            catch (FormatException ex)
            {
                Log.LogWarning("[{Conn}] {Tag}: {Error}", Config.Name, tag.Name, ex.Message);
                Publish(tag, null, TagQuality.Bad);
            }
        }

        _checksum = Enum.TryParse<ChecksumKind>(Config.Setting("Checksum", "None"), true, out var kind) ? kind : ChecksumKind.None;
        _checksumStart = Config.SettingInt("ChecksumStart", 0);

        _assembler = new FrameAssembler
        {
            Header = FrameAssembler.ParseHex(Config.Setting("Header")),
            FixedLength = Config.SettingInt("FrameLength", 0),
            LengthOffset = Config.SettingInt("LengthOffset", 2),
            LengthSize = Config.SettingInt("LengthSize", 1) == 2 ? 2 : 1,
            LengthBigEndian = _bigEndian,
            LengthAdjust = Config.SettingInt("LengthAdjust", 0),
            Checksum = _checksum,
            ChecksumStart = _checksumStart,
        };

        // Receive-only stream: one device per port.
        _lease = SerialPortHub.Acquire(Config, exclusive: true);
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
        var port = _port;
        if (port is not { IsOpen: true }) throw new IOException("Serial port closed.");
        if (_request.Length > 0) Send(port, _request);
        return Task.CompletedTask;
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var port = _port;
            if (port is null) return;
            var n = port.BytesToRead;
            if (n <= 0) return;
            var chunk = new byte[n];
            n = port.Read(chunk, 0, n);

            IReadOnlyList<byte[]> frames;
            lock (_lock) frames = _assembler.Push(chunk.AsSpan(0, n));
            foreach (var f in frames) HandleFrame(f);
        }
        catch (Exception ex)
        {
            Log.LogWarning("[{Conn}] serial read: {Error}", Config.Name, ex.Message);
        }
    }

    internal void HandleFrame(byte[] frame)
    {
        foreach (var (tag, spec) in _specs)
        {
            var value = spec.Decode(frame, _bigEndian);
            if (value is null)
            {
                if (_badFrames++ % 100 == 0)
                    Log.LogWarning("[{Conn}] {Tag}: frame ({Len} bytes) too short for '{Addr}'", Config.Name, tag.Name, frame.Length, tag.Address);
                Publish(tag, null, TagQuality.Bad);
            }
            else Publish(tag, value);
        }
    }

    private void Send(SerialPort port, byte[] bytes)
    {
        lock (_txLock) port.Write(bytes, 0, bytes.Length);
    }

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var port = _port is { IsOpen: true } p ? p : throw new InvalidOperationException("Not connected.");
        var (_, template) = BinaryFieldSpec.SplitAddress(tag.Address);
        if (string.IsNullOrEmpty(template))
            throw new InvalidOperationException("Tag has no write template (address: offset:type[:scale]|AA55 05 {v} {ck}).");
        if (!_specs.TryGetValue(tag, out var spec))
            throw new InvalidOperationException("Tag address is invalid.");

        var frame = WriteTemplate.Build(template, spec.Encode(value, _bigEndian), _checksum, _checksumStart);
        Send(port, frame);
        return Task.CompletedTask;
    }
}
