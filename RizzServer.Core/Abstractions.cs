namespace RizzServer.Core;

public interface IDriver : IAsyncDisposable
{
    event Action<TagConfig, object?, TagQuality>? TagUpdated;
    event Action<ConnectionState, string?>? StateChanged;

    Task StartAsync();
    Task StopAsync();
    Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default);
}

public interface IDriverFactory
{
    /// <summary>Creates a driver for one device. <paramref name="config"/> has channel and device settings merged.</summary>
    IDriver Create(ConnectionConfig config);
}

/// <summary>Whether a setting belongs to the channel (shared link) or to one device on it.</summary>
public enum SettingScope { Channel, Device }

public sealed record SettingDescriptor(
    string Key, string Label, string Default, SettingScope Scope,
    string[]? Options = null, bool Secret = false);

public sealed record DriverInfo(DriverType Type, string DisplayName, string AddressHint, SettingDescriptor[] Settings)
{
    /// <summary>Maximum devices per channel; 0 = unlimited.</summary>
    public int MaxDevices { get; init; }

    public IEnumerable<SettingDescriptor> ChannelSettings => Settings.Where(s => s.Scope == SettingScope.Channel);
    public IEnumerable<SettingDescriptor> DeviceSettings => Settings.Where(s => s.Scope == SettingScope.Device);
}

/// <summary>Static description of every driver so the UI can render its settings forms generically.</summary>
public static class DriverCatalog
{
    private static readonly string[] Parities = ["None", "Even", "Odd", "Mark", "Space"];
    private static readonly string[] StopBits = ["One", "OnePointFive", "Two"];
    private static readonly string[] Bools = ["False", "True"];

    private static SettingDescriptor Ch(string key, string label, string def, string[]? options = null, bool secret = false) =>
        new(key, label, def, SettingScope.Channel, options, secret);

    private static SettingDescriptor Dv(string key, string label, string def, string[]? options = null, bool secret = false) =>
        new(key, label, def, SettingScope.Device, options, secret);

    private static SettingDescriptor[] SerialPort() =>
    [
        Ch("PortName", "COM port", "COM1"),
        Ch("BaudRate", "Baud rate", "9600"),
        Ch("DataBits", "Data bits", "8"),
        Ch("Parity", "Parity", "None", Parities),
        Ch("StopBits", "Stop bits", "One", StopBits),
    ];

    public static IReadOnlyList<DriverInfo> All { get; } =
    [
        new(DriverType.ModbusTcp, "Modbus TCP",
            "HR100 (holding reg), IR100 (input reg), C5 (coil), DI5 (discrete input). Zero-based.",
            [
                Ch("TimeoutMs", "Timeout (ms)", "2000"),
                Dv("Host", "Host", "127.0.0.1"),
                Dv("Port", "Port", "502"),
                Dv("UnitId", "Unit ID", "1"),
                Dv("WordSwap", "Swap words (32/64-bit)", "False", Bools),
            ]),
        new(DriverType.ModbusRtu, "Modbus RTU (RS-232/485)",
            "HR100 (holding reg), IR100 (input reg), C5 (coil), DI5 (discrete input). Zero-based. Devices on one channel share the serial bus (multi-drop).",
            [
                .. SerialPort(),
                Ch("TimeoutMs", "Timeout (ms)", "1000"),
                Dv("UnitId", "Unit ID", "1"),
                Dv("WordSwap", "Swap words (32/64-bit)", "False", Bools),
            ]),
        new(DriverType.Serial, "Serial (RS-232 text)",
            "Field index in the received line (0,1,2…), 'line' for the whole line, or 're:pattern' (first capture group).",
            [
                .. SerialPort(),
                Dv("Terminator", "Line terminator", "\\r\\n"),
                Dv("Separator", "Field separator", ","),
            ]) { MaxDevices = 1 },
        new(DriverType.Rs232Poll, "RS-232 command/response",
            "command|parse|writeTemplate. E.g. READ?|0 (field 0 of reply), W|re:([-\\d.]+) (regex), SP?| (whole reply), SP?||SP {0} (adds write). Escapes: \\r \\n \\xHH.",
            [
                .. SerialPort(),
                Dv("CommandTerminator", "Command terminator", "\\r"),
                Dv("Terminator", "Reply terminator", "\\r\\n"),
                Dv("Separator", "Field separator", ","),
                Dv("TimeoutMs", "Reply timeout (ms)", "1000"),
                Dv("InterCommandDelayMs", "Delay between commands (ms)", "50"),
            ]),
        new(DriverType.BinaryFrame, "RS-232 binary frames",
            "offset:type[:scale] — types u8 i8 u16 i16 u32 i32 u64 i64 f32 f64 (+le/be), bit0..bit7, ascii8. E.g. 4:i16:0.1 or 6:f32le. To write add |template with {v} (value) and {ck} (checksum): 4:i16:0.1|AA55 05 {v} {ck}.",
            [
                .. SerialPort(),
                Dv("Header", "Frame header (hex)", "AA55"),
                Dv("FrameLength", "Fixed frame length (0 = length field)", "10"),
                Dv("LengthOffset", "Length field offset", "2"),
                Dv("LengthSize", "Length field size", "1", ["1", "2"]),
                Dv("LengthAdjust", "Length adjust (added to field)", "0"),
                Dv("ByteOrder", "Byte order", "BigEndian", ["BigEndian", "LittleEndian"]),
                Dv("Checksum", "Checksum (at frame end)", "None", ["None", "Sum8", "Xor8", "Crc16Modbus"]),
                Dv("ChecksumStart", "Checksum starts at byte", "0"),
                Dv("Request", "Poll request (hex, optional)", ""),
            ]) { MaxDevices = 1 },
        new(DriverType.OpcUa, "OPC UA client",
            "NodeId in string form, e.g. ns=2;s=Pressure or i=2258. Values arrive by subscription at the device scan rate.",
            [
                Ch("EndpointUrl", "Endpoint URL", "opc.tcp://localhost:4840"),
                Ch("Security", "Security", "None", ["None", "Secure"]),
                Ch("Username", "Username (blank = anonymous)", ""),
                Ch("Password", "Password", "", secret: true),
                Ch("AutoAcceptCertificates", "Auto-accept server certificate", "True", Bools),
            ]),
        new(DriverType.Mqtt, "MQTT",
            "Topic, optionally followed by |json.path (e.g. plant/line1|sensor.temp). Wildcards + and # supported.",
            [
                Ch("Host", "Broker host", "localhost"),
                Ch("Port", "Port", "1883"),
                Ch("ClientId", "Client ID prefix", "rizzserver"),
                Ch("Username", "Username", ""),
                Ch("Password", "Password", "", secret: true),
                Ch("UseTls", "Use TLS", "False", Bools),
            ]),
        new(DriverType.OpcDa, "OPC DA (classic)",
            "OPC item ID, e.g. Random.Int4. Requires OPC Core Components and the OPC Automation wrapper.",
            [
                Ch("ProgId", "Server ProgID", "Matrikon.OPC.Simulation.1"),
                Ch("Node", "Node / host", ""),
            ]),
    ];

    public static DriverInfo Get(DriverType type) => All.First(d => d.Type == type);
}
