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
    IDriver Create(ConnectionConfig config);
}

public sealed record SettingDescriptor(string Key, string Label, string Default, string[]? Options = null, bool Secret = false);

public sealed record DriverInfo(DriverType Type, string DisplayName, string AddressHint, SettingDescriptor[] Settings);

/// <summary>Static description of every driver so the UI can render its settings form generically.</summary>
public static class DriverCatalog
{
    private static readonly string[] Parities = ["None", "Even", "Odd", "Mark", "Space"];
    private static readonly string[] StopBits = ["One", "OnePointFive", "Two"];
    private static readonly string[] Bools = ["False", "True"];

    public static IReadOnlyList<DriverInfo> All { get; } =
    [
        new(DriverType.ModbusTcp, "Modbus TCP",
            "HR100 (holding reg), IR100 (input reg), C5 (coil), DI5 (discrete input). Zero-based.",
            [
                new("Host", "Host", "127.0.0.1"),
                new("Port", "Port", "502"),
                new("UnitId", "Unit ID", "1"),
                new("TimeoutMs", "Timeout (ms)", "2000"),
                new("WordSwap", "Swap words (32/64-bit)", "False", Bools),
            ]),
        new(DriverType.ModbusRtu, "Modbus RTU (RS-232/485)",
            "HR100 (holding reg), IR100 (input reg), C5 (coil), DI5 (discrete input). Zero-based.",
            [
                new("PortName", "COM port", "COM1"),
                new("BaudRate", "Baud rate", "9600"),
                new("DataBits", "Data bits", "8"),
                new("Parity", "Parity", "None", Parities),
                new("StopBits", "Stop bits", "One", StopBits),
                new("UnitId", "Unit ID", "1"),
                new("TimeoutMs", "Timeout (ms)", "1000"),
                new("WordSwap", "Swap words (32/64-bit)", "False", Bools),
            ]),
        new(DriverType.Serial, "Serial (RS-232 text)",
            "Field index in the received line (0,1,2…), 'line' for the whole line, or 're:pattern' (first capture group).",
            [
                new("PortName", "COM port", "COM1"),
                new("BaudRate", "Baud rate", "9600"),
                new("DataBits", "Data bits", "8"),
                new("Parity", "Parity", "None", Parities),
                new("StopBits", "Stop bits", "One", StopBits),
                new("Terminator", "Line terminator", "\\r\\n"),
                new("Separator", "Field separator", ","),
            ]),
        new(DriverType.Rs232Poll, "RS-232 command/response",
            "command|parse|writeTemplate. E.g. READ?|0 (field 0 of reply), W|re:([-\\d.]+) (regex), SP?| (whole reply), SP?||SP {0} (adds write). Escapes: \\r \\n \\xHH.",
            [
                new("PortName", "COM port", "COM1"),
                new("BaudRate", "Baud rate", "9600"),
                new("DataBits", "Data bits", "8"),
                new("Parity", "Parity", "None", Parities),
                new("StopBits", "Stop bits", "One", StopBits),
                new("CommandTerminator", "Command terminator", "\\r"),
                new("Terminator", "Reply terminator", "\\r\\n"),
                new("Separator", "Field separator", ","),
                new("TimeoutMs", "Reply timeout (ms)", "1000"),
                new("InterCommandDelayMs", "Delay between commands (ms)", "50"),
            ]),
        new(DriverType.BinaryFrame, "RS-232 binary frames",
            "offset:type[:scale] — types u8 i8 u16 i16 u32 i32 u64 i64 f32 f64 (+le/be), bit0..bit7, ascii8. E.g. 4:i16:0.1 or 6:f32le. To write add |template with {v} (value) and {ck} (checksum): 4:i16:0.1|AA55 05 {v} {ck}.",
            [
                new("PortName", "COM port", "COM1"),
                new("BaudRate", "Baud rate", "9600"),
                new("DataBits", "Data bits", "8"),
                new("Parity", "Parity", "None", Parities),
                new("StopBits", "Stop bits", "One", StopBits),
                new("Header", "Frame header (hex)", "AA55"),
                new("FrameLength", "Fixed frame length (0 = length field)", "10"),
                new("LengthOffset", "Length field offset", "2"),
                new("LengthSize", "Length field size", "1", ["1", "2"]),
                new("LengthAdjust", "Length adjust (added to field)", "0"),
                new("ByteOrder", "Byte order", "BigEndian", ["BigEndian", "LittleEndian"]),
                new("Checksum", "Checksum (at frame end)", "None", ["None", "Sum8", "Xor8", "Crc16Modbus"]),
                new("ChecksumStart", "Checksum starts at byte", "0"),
                new("Request", "Poll request (hex, optional)", ""),
            ]),
        new(DriverType.OpcUa, "OPC UA client",
            "NodeId in string form, e.g. ns=2;s=Pressure or i=2258. Values arrive by subscription at the poll interval.",
            [
                new("EndpointUrl", "Endpoint URL", "opc.tcp://localhost:4840"),
                new("Security", "Security", "None", ["None", "Secure"]),
                new("Username", "Username (blank = anonymous)", ""),
                new("Password", "Password", "", Secret: true),
                new("AutoAcceptCertificates", "Auto-accept server certificate", "True", Bools),
            ]),
        new(DriverType.Mqtt, "MQTT",
            "Topic, optionally followed by |json.path (e.g. plant/line1|sensor.temp). Wildcards + and # supported.",
            [
                new("Host", "Broker host", "localhost"),
                new("Port", "Port", "1883"),
                new("ClientId", "Client ID", "rizzserver"),
                new("Username", "Username", ""),
                new("Password", "Password", "", Secret: true),
                new("UseTls", "Use TLS", "False", Bools),
            ]),
        new(DriverType.OpcDa, "OPC DA (classic)",
            "OPC item ID, e.g. Random.Int4. Requires OPC Core Components and the OPC Automation wrapper.",
            [
                new("ProgId", "Server ProgID", "Matrikon.OPC.Simulation.1"),
                new("Node", "Node / host", ""),
            ]),
    ];

    public static DriverInfo Get(DriverType type) => All.First(d => d.Type == type);
}
