using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// RS-232 binary protocol: frames are synchronised on a header, optionally validated by a checksum,
/// and each tag decodes a field at a byte offset (address <c>offset:type[:scale]</c>, e.g. <c>4:i16:0.1</c>).
/// An optional request is sent every poll for devices that must be triggered.
/// </summary>
public sealed class BinaryFrameDriver : DriverBase
{
    private readonly object _lock = new();
    private SerialPort? _port;
    private FrameAssembler _assembler = null!;
    private Dictionary<TagConfig, BinaryFieldSpec> _specs = new();
    private bool _bigEndian = true;
    private byte[] _request = [];
    private int _badFrames;

    public BinaryFrameDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    protected override Task ConnectAsync(CancellationToken ct)
    {
        _bigEndian = !Config.Setting("ByteOrder", "BigEndian").Equals("LittleEndian", StringComparison.OrdinalIgnoreCase);
        _request = FrameAssembler.ParseHex(Config.Setting("Request"));

        _specs = new Dictionary<TagConfig, BinaryFieldSpec>();
        foreach (var tag in Tags)
        {
            try { _specs[tag] = BinaryFieldSpec.Parse(tag.Address); }
            catch (FormatException ex)
            {
                Log.LogWarning("[{Conn}] {Tag}: {Error}", Config.Name, tag.Name, ex.Message);
                Publish(tag, null, TagQuality.Bad);
            }
        }

        _assembler = new FrameAssembler
        {
            Header = FrameAssembler.ParseHex(Config.Setting("Header")),
            FixedLength = Config.SettingInt("FrameLength", 0),
            LengthOffset = Config.SettingInt("LengthOffset", 2),
            LengthSize = Config.SettingInt("LengthSize", 1) == 2 ? 2 : 1,
            LengthBigEndian = _bigEndian,
            LengthAdjust = Config.SettingInt("LengthAdjust", 0),
            Checksum = Enum.TryParse<ChecksumKind>(Config.Setting("Checksum", "None"), true, out var ck) ? ck : ChecksumKind.None,
            ChecksumStart = Config.SettingInt("ChecksumStart", 0),
        };

        _port = new SerialPort(Config.Setting("PortName", "COM1"), Config.SettingInt("BaudRate", 9600),
            Enum.Parse<Parity>(Config.Setting("Parity", "None")), Config.SettingInt("DataBits", 8),
            Enum.Parse<StopBits>(Config.Setting("StopBits", "One")));
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
        var port = _port;
        if (port is not { IsOpen: true }) throw new IOException("Serial port closed.");
        if (_request.Length > 0) port.Write(_request, 0, _request.Length);
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

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default) =>
        throw new NotSupportedException("The binary frame driver is read-only.");
}
