using System.Buffers.Binary;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NModbus;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>Modbus master over TCP or RTU (serial). Address: HR100, IR100, C5, DI5 (zero-based).</summary>
public sealed partial class ModbusDriver : DriverBase
{
    private readonly bool _rtu;
    private readonly SemaphoreSlim _io = new(1, 1);
    private IModbusMaster? _master;
    private TcpClient? _tcp;
    private SerialPort? _port;

    public ModbusDriver(ConnectionConfig config, ILogger log, bool rtu) : base(config, log) => _rtu = rtu;

    private byte UnitId => (byte)Config.SettingInt("UnitId", 1);
    private bool WordSwap => Config.SettingBool("WordSwap");

    [GeneratedRegex(@"^\s*(HR|IR|C|DI)\s*(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    private static (string Area, ushort Offset) ParseAddress(string address)
    {
        var m = AddressRegex().Match(address);
        if (!m.Success) throw new FormatException($"Bad Modbus address '{address}' (use HR100, IR100, C5, DI5).");
        return (m.Groups[1].Value.ToUpperInvariant(), ushort.Parse(m.Groups[2].Value));
    }

    protected override async Task ConnectAsync(CancellationToken ct)
    {
        var timeout = Config.SettingInt("TimeoutMs", 2000);
        var factory = new ModbusFactory();
        if (_rtu)
        {
            _port = new SerialPort(Config.Setting("PortName", "COM1"), Config.SettingInt("BaudRate", 9600),
                Enum.Parse<Parity>(Config.Setting("Parity", "None")), Config.SettingInt("DataBits", 8),
                Enum.Parse<StopBits>(Config.Setting("StopBits", "One")));
            _port.Open();
            _master = factory.CreateRtuMaster(new SerialPortAdapter(_port));
        }
        else
        {
            _tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await _tcp.ConnectAsync(Config.Setting("Host", "127.0.0.1"), Config.SettingInt("Port", 502), cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Connect to {Config.Setting("Host")}:{Config.SettingInt("Port", 502)} timed out.");
            }
            _master = factory.CreateMaster(_tcp);
        }
        _master.Transport.ReadTimeout = timeout;
        _master.Transport.WriteTimeout = timeout;
        _master.Transport.Retries = 1;
    }

    protected override Task DisconnectAsync()
    {
        _master?.Dispose();
        _master = null;
        _tcp?.Dispose();
        _tcp = null;
        _port?.Dispose();
        _port = null;
        return Task.CompletedTask;
    }

    protected override async Task PollAsync(CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            foreach (var tag in Tags)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Publish(tag, await ReadTagAsync(tag));
                }
                catch (SlaveException ex)
                {
                    // Device rejected this address; keep the link up and flag only this tag.
                    Log.LogWarning("[{Conn}] {Tag}: {Error}", Config.Name, tag.Name, ex.Message);
                    Publish(tag, null, TagQuality.Bad);
                }
                catch (FormatException ex)
                {
                    Log.LogWarning("[{Conn}] {Tag}: {Error}", Config.Name, tag.Name, ex.Message);
                    Publish(tag, null, TagQuality.Bad);
                }
            }
        }
        finally { _io.Release(); }
    }

    private async Task<object?> ReadTagAsync(TagConfig tag)
    {
        var master = _master ?? throw new InvalidOperationException("Not connected.");
        var (area, offset) = ParseAddress(tag.Address);
        switch (area)
        {
            case "C": return (await master.ReadCoilsAsync(UnitId, offset, 1))[0];
            case "DI": return (await master.ReadInputsAsync(UnitId, offset, 1))[0];
        }

        var count = TagValueConverter.RegisterCount(tag.DataType);
        var regs = area == "HR"
            ? await master.ReadHoldingRegistersAsync(UnitId, offset, count)
            : await master.ReadInputRegistersAsync(UnitId, offset, count);
        return DecodeRegisters(regs, tag.DataType);
    }

    private object DecodeRegisters(ushort[] regs, TagDataType type)
    {
        if (type == TagDataType.String) throw new FormatException("String tags are not supported over Modbus.");
        if (type == TagDataType.Boolean) return regs[0] != 0;

        var ordered = WordSwap ? regs.Reverse().ToArray() : regs;
        Span<byte> b = stackalloc byte[ordered.Length * 2];
        for (var i = 0; i < ordered.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b[(i * 2)..], ordered[i]);

        return type switch
        {
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(b),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(b),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(b),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b),
            TagDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(b),
            TagDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(b),
            TagDataType.Float => BinaryPrimitives.ReadSingleBigEndian(b),
            TagDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(b),
            _ => throw new NotSupportedException(type.ToString()),
        };
    }

    private ushort[] EncodeRegisters(object value, TagDataType type)
    {
        Span<byte> b = stackalloc byte[8];
        switch (type)
        {
            case TagDataType.Boolean: return [(ushort)((bool)value ? 1 : 0)];
            case TagDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(b, (short)value); break;
            case TagDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value); break;
            case TagDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(b, (int)value); break;
            case TagDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value); break;
            case TagDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(b, (long)value); break;
            case TagDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)value); break;
            case TagDataType.Float: BinaryPrimitives.WriteSingleBigEndian(b, (float)value); break;
            case TagDataType.Double: BinaryPrimitives.WriteDoubleBigEndian(b, (double)value); break;
            default: throw new NotSupportedException($"{type} cannot be written over Modbus.");
        }

        var n = TagValueConverter.RegisterCount(type);
        var regs = new ushort[n];
        for (var i = 0; i < n; i++) regs[i] = BinaryPrimitives.ReadUInt16BigEndian(b[(i * 2)..]);
        return WordSwap ? regs.Reverse().ToArray() : regs;
    }

    public override async Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var master = _master ?? throw new InvalidOperationException("Not connected.");
        var (area, offset) = ParseAddress(tag.Address);
        await _io.WaitAsync(ct);
        try
        {
            switch (area)
            {
                case "C":
                    await master.WriteSingleCoilAsync(UnitId, offset, Convert.ToBoolean(value));
                    break;
                case "HR":
                    var regs = EncodeRegisters(value, tag.DataType);
                    if (regs.Length == 1) await master.WriteSingleRegisterAsync(UnitId, offset, regs[0]);
                    else await master.WriteMultipleRegistersAsync(UnitId, offset, regs);
                    break;
                default:
                    throw new InvalidOperationException($"Area {area} is read-only.");
            }
        }
        finally { _io.Release(); }
    }
}
