using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.App.ViewModels;
using RizzServer.Core;
using RizzServer.Drivers;

namespace RizzServer.App.Designer;

public enum FramingMode { Header, IdleGap }

/// <summary>One captured frame as a row of hex cells.</summary>
public sealed class FrameRow
{
    public required int Index { get; init; }
    public required string Time { get; init; }
    public required byte[] Bytes { get; init; }
    public required string[] Cells { get; init; }
}

/// <summary>A value the user wants as a tag: a byte offset, a reading and optionally a scale.</summary>
public sealed partial class DesignField : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _offset;
    [ObservableProperty] private string _type = "u16";
    [ObservableProperty] private string _scale = "1";
    [ObservableProperty] private string _value = "";

    public int Size => Type.ToLowerInvariant().TrimEnd('l', 'e') switch
    {
        "u8" or "i8" => 1,
        "u16" or "i16" => 2,
        "u32" or "i32" or "f32" => 4,
        "u64" or "i64" or "f64" => 8,
        var t when t.StartsWith("bit") => 1,
        var t when t.StartsWith("ascii") && int.TryParse(t[5..], out var n) => n,
        _ => 1,
    };

    /// <summary>Address in the binary driver's syntax: offset:type[:scale].</summary>
    public string Address => Scale.Trim() is { Length: > 0 } s && s != "1" ? $"{Offset}:{Type}:{s}" : $"{Offset}:{Type}";
}

/// <summary>One way of reading the bytes at the inspected offset.</summary>
public sealed class Reading
{
    public required string Type { get; init; }
    public required string Last { get; init; }
    public required string Min { get; init; }
    public required string Max { get; init; }
    public required int Distinct { get; init; }
    public required int Smoothness { get; init; }   // 0-100: how steadily the value moves from frame to frame
}

/// <summary>
/// Reverse-engineering a binary serial protocol: capture bytes, find the framing, see what each byte does,
/// name the interesting values and turn them into a ready-to-run channel for the binary frame driver.
/// </summary>
public sealed partial class ProtocolDesignerViewModel : ObservableObject
{
    private const int MaxRows = 300;
    private const int MaxFrames = 5000;

    private readonly object _lock = new();
    private readonly List<(DateTime Time, byte[] Data)> _chunks = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private IByteSource? _source;
    private bool _dirty;
    private bool _autoDetected;
    private List<byte[]> _frames = [];

    public ProtocolDesignerViewModel()
    {
        _refresh.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Reframe(autoDetectIfNeeded: true);
        };
        _refresh.Start();
        Text = new TextDesignerViewModel(this);
        RefreshPorts();
    }

    /// <summary>The text / command-response half of the designer.</summary>
    public TextDesignerViewModel Text { get; }

    /// <summary>0 = binary frames, 1 = text and commands; decides what "Create channel" builds.</summary>
    [ObservableProperty] private int _activeTab;

    /// <summary>Writes to the device through the open capture (terminal commands). False when nothing is open.</summary>
    public bool TrySend(byte[] data)
    {
        if (_source is null) return false;
        try { _source.Send(data); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    public AppState App => AppState.Instance;

    // ---- source ----
    public ObservableCollection<string> Ports { get; } = new();
    public IReadOnlyList<string> Parities { get; } = ["None", "Even", "Odd", "Mark", "Space"];
    public IReadOnlyList<string> StopBitsOptions { get; } = ["One", "OnePointFive", "Two"];

    [ObservableProperty] private string _portName = "COM1";
    [ObservableProperty] private string _baudRate = "9600";
    [ObservableProperty] private string _dataBits = "8";
    [ObservableProperty] private string _parity = "None";
    [ObservableProperty] private string _stopBits = "One";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CaptureButtonText))] private bool _isCapturing;
    [ObservableProperty] private string _sourceStatus = "No data yet. Capture from a serial port, open a capture file or paste hex.";
    [ObservableProperty] private int _totalBytes;

    public string CaptureButtonText => IsCapturing ? "Stop capture" : "Start capture";

    /// <summary>Remote projects: the service's serial ports can't be reached from this machine.</summary>
    public bool CanCapture => !App.IsRemote;

    public void RefreshPorts()
    {
        Ports.Clear();
        try { foreach (var p in SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Ports.Add(p); }
        catch { /* no serial support on this system */ }
        if (Ports.Count > 0 && !Ports.Contains(PortName)) PortName = Ports[0];
    }

    /// <summary>Starts capturing from any byte source (the serial port in the app, a fake in tests).</summary>
    public void StartCapture(IByteSource source)
    {
        StopCapture();
        _source = source;
        source.DataReceived += OnData;
        try
        {
            source.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            source.DataReceived -= OnData;
            source.Dispose();
            _source = null;
            SourceStatus = "Could not open the port: " + ex.Message;
            return;
        }
        IsCapturing = true;
        SourceStatus = "Capturing…";
    }

    public void StopCapture()
    {
        if (_source is null) return;
        _source.DataReceived -= OnData;
        _source.Dispose();
        _source = null;
        Text.StopPolling();
        IsCapturing = false;
        SourceStatus = $"Stopped. {TotalBytes} bytes captured.";
        Reframe(autoDetectIfNeeded: true);
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (IsCapturing) { StopCapture(); return; }
        if (!int.TryParse(BaudRate, out var baud) || !int.TryParse(DataBits, out var bits))
        {
            SourceStatus = "Baud rate and data bits must be numbers.";
            return;
        }
        StartCapture(new SerialByteSource(PortName, baud, bits, Enum.Parse<Parity>(Parity), Enum.Parse<StopBits>(StopBits)));
    }

    private void OnData(byte[] data, DateTime time)
    {
        lock (_lock) _chunks.Add((time, data));
        Text.OnReceived(data, time);
        Dispatcher.UIThread.Post(() => { TotalBytes += data.Length; });
        _dirty = true;
    }

    /// <summary>Adds bytes that were not captured live (a file or pasted hex).</summary>
    public void LoadBytes(byte[] bytes, string description)
    {
        StopCapture();
        lock (_lock)
        {
            _chunks.Clear();
            _chunks.Add((DateTime.Now, bytes));
        }
        TotalBytes = bytes.Length;
        _autoDetected = false;
        Text.LoadText(bytes);
        SourceStatus = $"{bytes.Length} bytes from {description}.";
        Reframe(autoDetectIfNeeded: true);
    }

    [RelayCommand]
    private void Clear()
    {
        StopCapture();
        lock (_lock) _chunks.Clear();
        Text.Reset();
        TotalBytes = 0;
        _autoDetected = false;
        _frames = [];
        SourceStatus = "Cleared.";
        UpdateFrames();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (FileService.PickOpen is not { } pick) return;
        var path = await pick("Open a capture (raw bytes or hex text)");
        if (path is null) return;
        try
        {
            if (!CaptureFile.TryLoad(await File.ReadAllBytesAsync(path), out var bytes)) { SourceStatus = "The file is empty."; return; }
            LoadBytes(bytes, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SourceStatus = "Could not read the file: " + ex.Message; }
    }

    [RelayCommand]
    private async Task PasteHexAsync()
    {
        if (FileService.PasteText is not { } paste) return;
        var text = await paste();
        if (text is null || !HexText.TryParse(text, out var bytes)) { SourceStatus = "The clipboard has no hex bytes."; return; }
        LoadBytes(bytes, "the clipboard");
    }

    [RelayCommand]
    private async Task SaveCaptureAsync()
    {
        if (FileService.PickSave is not { } pick || RawBytes().Length == 0) return;
        var path = await pick("Save the capture as hex text", "capture.hex");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine,
                RawBytes().Chunk(16).Select(c => string.Join(' ', c.Select(b => b.ToString("X2"))))));
            SourceStatus = "Saved " + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SourceStatus = "Could not save: " + ex.Message; }
    }

    public byte[] RawBytes()
    {
        lock (_lock) return _chunks.SelectMany(c => c.Data).ToArray();
    }

    // ---- framing ----
    [ObservableProperty] private FramingMode _mode = FramingMode.Header;
    [ObservableProperty] private string _headerHex = "";
    [ObservableProperty] private string _frameLength = "0";
    [ObservableProperty] private string _lengthOffset = "2";
    [ObservableProperty] private string _lengthSize = "1";
    [ObservableProperty] private string _lengthAdjust = "0";
    [ObservableProperty] private string _byteOrder = "BigEndian";
    [ObservableProperty] private string _checksum = "None";
    [ObservableProperty] private string _checksumStart = "0";
    [ObservableProperty] private string _idleGapMs = "50";
    [ObservableProperty] private string _framingSummary = "";

    public IReadOnlyList<FramingMode> Modes { get; } = Enum.GetValues<FramingMode>();
    public IReadOnlyList<string> Checksums { get; } = ["None", "Sum8", "Xor8", "Crc16Modbus"];
    public IReadOnlyList<string> ByteOrders { get; } = ["BigEndian", "LittleEndian"];

    /// <summary>Looks at the capture and fills in the framing.</summary>
    [RelayCommand]
    private void Detect()
    {
        var raw = RawBytes();
        if (FrameDetector.Detect(raw) is not { } guess)
        {
            FramingSummary = "No repeating structure found. Capture more data, or enter the header and length by hand.";
            return;
        }
        ApplyGuess(guess);
        Reframe(autoDetectIfNeeded: false);
        FramingSummary = guess.Describe();
    }

    private void ApplyGuess(FramingGuess g)
    {
        Mode = FramingMode.Header;
        HeaderHex = Convert.ToHexString(g.Header);
        FrameLength = g.FrameLength.ToString(CultureInfo.InvariantCulture);
        LengthOffset = g.LengthOffset.ToString(CultureInfo.InvariantCulture);
        LengthSize = g.LengthSize.ToString(CultureInfo.InvariantCulture);
        LengthAdjust = g.LengthAdjust.ToString(CultureInfo.InvariantCulture);
        ByteOrder = g.LengthBigEndian ? "BigEndian" : "LittleEndian";
        Checksum = g.Checksum.ToString();
        ChecksumStart = g.ChecksumStart.ToString(CultureInfo.InvariantCulture);
        _autoDetected = true;
    }

    [RelayCommand]
    private void Apply() => Reframe(autoDetectIfNeeded: false);

    public FramingGuess CurrentFraming() => new(
        FrameAssembler.ParseHex(HeaderHex),
        Int(FrameLength), Int(LengthOffset), Int(LengthSize) == 2 ? 2 : 1,
        ByteOrder != "LittleEndian", Int(LengthAdjust),
        Enum.TryParse<ChecksumKind>(Checksum, true, out var kind) ? kind : ChecksumKind.None,
        Int(ChecksumStart), _frames.Count, 1);

    private static byte[] SafeHex(string text)
    {
        try { return FrameAssembler.ParseHex(text); }
        catch (FormatException) { return []; }
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void Reframe(bool autoDetectIfNeeded)
    {
        var raw = RawBytes();
        if (raw.Length == 0) { _frames = []; UpdateFrames(); return; }

        // First time there is enough data and nothing was set up: work the framing out.
        if (autoDetectIfNeeded && !_autoDetected && Mode == FramingMode.Header && HeaderHex.Length == 0 && raw.Length >= 256
            && FrameDetector.Detect(raw) is { } guess)
        {
            ApplyGuess(guess);
            FramingSummary = guess.Describe();
        }

        try
        {
            if (Mode == FramingMode.IdleGap)
            {
                List<(DateTime, byte[])> chunks;
                lock (_lock) chunks = [.. _chunks];
                _frames = FrameAnalysis.SplitByIdleGap(chunks, TimeSpan.FromMilliseconds(Math.Max(1, Int(IdleGapMs))));
                FramingSummary = $"{_frames.Count} frames split at silences of {IdleGapMs} ms or more.";
            }
            else if (FrameAssembler.ParseHex(HeaderHex).Length > 0 || Int(FrameLength) > 0)
            {
                _frames = FrameAnalysis.Cut(raw, CurrentFraming(), out var dropped);
                if (FramingSummary.Length == 0 || !_autoDetected) FramingSummary = $"{_frames.Count} frames, {dropped} bytes did not fit.";
            }
            else _frames = [];
        }
        catch (FormatException)
        {
            FramingSummary = "The header must be hex bytes, for example AA55.";
            _frames = [];
        }

        if (_frames.Count > MaxFrames) _frames = _frames.TakeLast(MaxFrames).ToList();
        UpdateFrames();
    }

    // ---- frames table ----
    public ObservableCollection<FrameRow> Rows { get; } = new();
    [ObservableProperty] private int _frameCount;
    [ObservableProperty] private int _maxLength;
    [ObservableProperty] private IBrush[] _columnBrushes = [];

    /// <summary>Raised when the number of byte columns or their roles changed, so the grid rebuilds its columns.</summary>
    public event Action? LayoutChanged;

    private void UpdateFrames()
    {
        FrameCount = _frames.Count;
        var length = _frames.Count == 0 ? 0 : _frames.Max(f => f.Length);

        Rows.Clear();
        var first = Math.Max(0, _frames.Count - MaxRows);
        for (var i = first; i < _frames.Count; i++)
        {
            var f = _frames[i];
            Rows.Add(new FrameRow
            {
                Index = i + 1,
                Time = "",
                Bytes = f,
                Cells = Enumerable.Range(0, length).Select(o => o < f.Length ? f[o].ToString("X2") : "").ToArray(),
            });
        }

        MaxLength = length;
        RefreshRoles();
        UpdateInspector();
        UpdateFieldValues();
    }

    // ---- byte roles (header / length / checksum / fields) ----
    public static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#552F6DB5"));
    public static readonly IBrush LengthBrush = new SolidColorBrush(Color.Parse("#66B7791F"));
    public static readonly IBrush ChecksumBrush = new SolidColorBrush(Color.Parse("#552E7D32"));
    public static readonly IBrush FieldBrush = new SolidColorBrush(Color.Parse("#558E44AD"));
    public static readonly IBrush InspectBrush = new SolidColorBrush(Color.Parse("#99F0A30A"));

    /// <summary>Role label of each byte column ("HDR", "LEN", "CHK", field name or "").</summary>
    [ObservableProperty] private string[] _columnRoles = [];

    private void RefreshRoles()
    {
        var brushes = new IBrush[MaxLength];
        var roles = new string[MaxLength];
        Array.Fill(brushes, Brushes.Transparent);
        Array.Fill(roles, "");

        void Mark(int offset, int size, IBrush brush, string role)
        {
            for (var i = offset; i < offset + size && i < MaxLength; i++)
                if (i >= 0) { brushes[i] = brush; roles[i] = role; }
        }

        if (Mode == FramingMode.Header)
        {
            var g = CurrentFraming();
            Mark(0, g.Header.Length, HeaderBrush, "HDR");
            if (!g.IsFixedLength && g.Header.Length > 0) Mark(g.LengthOffset, g.LengthSize, LengthBrush, "LEN");
            if (g.ChecksumLength > 0 && g.IsFixedLength) Mark(g.FrameLength - g.ChecksumLength, g.ChecksumLength, ChecksumBrush, "CHK");
        }
        foreach (var f in Fields) Mark(f.Offset, f.Size, FieldBrush, f.Name);

        ColumnBrushes = brushes;
        ColumnRoles = roles;
        LayoutChanged?.Invoke();
    }

    /// <summary>Bytes that are framing, not data: header, length field, checksum.</summary>
    private bool IsFramingByte(int o)
    {
        if (Mode != FramingMode.Header) return false;
        var g = CurrentFraming();
        if (o < g.Header.Length) return true;
        if (!g.IsFixedLength && o >= g.LengthOffset && o < g.LengthOffset + g.LengthSize) return true;
        if (g.ChecksumLength > 0 && g.IsFixedLength && o >= g.FrameLength - g.ChecksumLength) return true;
        return false;
    }

    // ---- inspector ----
    [ObservableProperty] private int _inspectOffset;
    [ObservableProperty] private Reading? _selectedReading;
    public ObservableCollection<Reading> Readings { get; } = new();
    [ObservableProperty] private string _inspectSummary = "Click a byte in the table to see how it reads.";

    partial void OnInspectOffsetChanged(int value) => UpdateInspector();

    private void UpdateInspector()
    {
        Readings.Clear();
        if (_frames.Count == 0 || InspectOffset < 0 || InspectOffset >= MaxLength)
        {
            InspectSummary = "Click a byte in the table to see how it reads.";
            return;
        }

        var frames = _frames.Where(f => f.Length > InspectOffset).TakeLast(500).ToList();
        var available = frames.Min(f => f.Length) - InspectOffset;
        var distinct = frames.Select(f => f[InspectOffset]).Distinct().Count();
        InspectSummary = $"Byte {InspectOffset}: {(distinct == 1 ? "constant " + frames[0][InspectOffset].ToString("X2") : distinct + " different values")} across {frames.Count} frames.";

        foreach (var type in new[] { "u8", "i8", "u16", "u16le", "i16", "i16le", "u32", "u32le", "i32", "i32le", "f32", "f32le" })
        {
            var size = type.StartsWith("u8") || type.StartsWith("i8") ? 1 : type.Contains("16") ? 2 : 4;
            if (size > available) continue;
            try
            {
                var spec = BinaryFieldSpec.Parse($"{InspectOffset}:{type}");
                var values = frames.Select(f => Convert.ToDouble(spec.Decode(f, true), CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(v => !double.IsFinite(v))) continue;
                var jitter = values.Distinct().Count() <= 1 ? 0 : FrameAnalysis.Jitter(values);
                // A float reading of the wrong bytes looks smooth but lands outside any physical range.
                var implausible = type.StartsWith("f32") && !values.All(v => v == 0 || Math.Abs(v) is > 1e-4 and < 1e9);
                Readings.Add(new Reading
                {
                    Type = type,
                    Last = Format(values[^1]),
                    Min = Format(values.Min()),
                    Max = Format(values.Max()),
                    Distinct = values.Distinct().Count(),
                    Smoothness = implausible ? 0 : (int)Math.Round(100 * Math.Clamp(1 - jitter / 0.33, 0, 1)),
                });
            }
            catch (FormatException) { /* type not valid here */ }
        }

        // Best reading first; on a tie prefer floats (a float reading that is smooth AND plausible is rarely a coincidence).
        var sorted = Readings.OrderByDescending(r => r.Smoothness).ThenBy(r => r.Type.StartsWith("f32") ? 0 : 1).ToList();
        Readings.Clear();
        foreach (var r in sorted) Readings.Add(r);
    }

    private static string Format(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15
        ? ((long)v).ToString(CultureInfo.InvariantCulture)
        : v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- fields ----
    public ObservableCollection<DesignField> Fields { get; } = new();
    [ObservableProperty] private DesignField? _selectedField;

    [RelayCommand]
    private void AddFieldFromReading()
    {
        if (SelectedReading is not { } r) return;
        AddField(InspectOffset, r.Type, null);
    }

    public DesignField AddField(int offset, string type, string? name)
    {
        var field = new DesignField { Offset = offset, Type = type, Name = name ?? UniqueName($"Value{offset}") };
        field.PropertyChanged += (_, _) => { RefreshRoles(); UpdateFieldValues(); };
        Fields.Add(field);
        SelectedField = field;
        RefreshRoles();
        UpdateFieldValues();
        return field;
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; Fields.Any(f => f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    [RelayCommand]
    private void RemoveField()
    {
        if (SelectedField is not { } f) return;
        Fields.Remove(f);
        SelectedField = null;
        RefreshRoles();
    }

    /// <summary>Offers fields for every changing byte; counters are skipped (they carry no measurement).</summary>
    [RelayCommand]
    private void SuggestFields()
    {
        var frames = _frames.Where(f => f.Length == MaxLength).TakeLast(500).ToList();
        if (frames.Count < 3) { FramingSummary = "Need at least three complete frames to suggest fields."; return; }

        var suggestions = FrameAnalysis.SuggestFields(frames, IsFramingByte).Where(s => !s.IsCounter).ToList();
        if (suggestions.Count == 0) { FramingSummary = "Nothing changes between frames, so there is nothing to suggest."; return; }

        foreach (var s in suggestions)
            if (!Fields.Any(f => f.Offset == s.Offset && f.Type == s.Type))
                AddField(s.Offset, s.Type, UniqueName(s.Name));
        FramingSummary = $"Suggested {suggestions.Count} field(s). Rename them and delete what you don't need.";
    }

    private void UpdateFieldValues()
    {
        var last = _frames.Count > 0 ? _frames[^1] : null;
        foreach (var f in Fields)
        {
            if (last is null) { f.Value = ""; continue; }
            try
            {
                var value = BinaryFieldSpec.Parse(f.Address).Decode(last, true);
                f.Value = value switch
                {
                    null => "(frame too short)",
                    double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                    float fl => fl.ToString("0.###", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                };
            }
            catch (FormatException) { f.Value = "(invalid)"; }
        }
    }

    // ---- output: a ready-to-run channel ----
    [ObservableProperty] private string _channelName = "";
    [ObservableProperty] private string _deviceName = "Device1";
    [ObservableProperty] private string _createMessage = "";

    /// <summary>The channel the design describes, validated like any other. Returns the problems when it isn't valid.</summary>
    public (ChannelConfig? Channel, List<string> Errors) BuildChannel()
    {
        var errors = new List<string>();
        if (Fields.Count == 0) errors.Add("Add at least one field first.");
        if (SafeHex(HeaderHex).Length == 0) errors.Add("The framing needs a header (run Detect or enter one).");

        var channel = new ChannelConfig { Name = ChannelName.Trim(), DriverType = DriverType.BinaryFrame };
        channel.Settings["PortName"] = PortName;
        channel.Settings["BaudRate"] = BaudRate;
        channel.Settings["DataBits"] = DataBits;
        channel.Settings["Parity"] = Parity;
        channel.Settings["StopBits"] = StopBits;

        var device = new DeviceConfig { Name = DeviceName.Trim(), ScanRateMs = 1000 };
        var g = CurrentFraming();
        device.Settings["Header"] = Convert.ToHexString(g.Header);
        device.Settings["FrameLength"] = g.FrameLength.ToString(CultureInfo.InvariantCulture);
        device.Settings["LengthOffset"] = g.LengthOffset.ToString(CultureInfo.InvariantCulture);
        device.Settings["LengthSize"] = g.LengthSize.ToString(CultureInfo.InvariantCulture);
        device.Settings["LengthAdjust"] = g.LengthAdjust.ToString(CultureInfo.InvariantCulture);
        device.Settings["ByteOrder"] = g.LengthBigEndian ? "BigEndian" : "LittleEndian";
        device.Settings["Checksum"] = g.Checksum.ToString();
        device.Settings["ChecksumStart"] = g.ChecksumStart.ToString(CultureInfo.InvariantCulture);
        device.Settings["Request"] = "";

        foreach (var f in Fields)
        {
            var scale = double.TryParse(f.Scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1;
            device.Tags.Add(new TagConfig
            {
                Name = f.Name.Trim(),
                Address = f.Address,
                DataType = TagTypeFor(f.Type, scale != 1),
                Access = TagAccess.ReadOnly,
            });
        }
        channel.Devices.Add(device);

        ConfigValidator.ValidateChannel(channel, App.Config.Channels.Select(c => c.Name), errors);
        return (errors.Count == 0 ? channel : null, errors);
    }

    private static TagDataType TagTypeFor(string type, bool scaled)
    {
        if (scaled) return TagDataType.Double;
        var t = type.ToLowerInvariant().TrimEnd('l', 'e');
        return t switch
        {
            "u8" or "u16" => TagDataType.UInt16,
            "i8" or "i16" => TagDataType.Int16,
            "u32" => TagDataType.UInt32,
            "i32" => TagDataType.Int32,
            "u64" => TagDataType.UInt64,
            "i64" => TagDataType.Int64,
            "f32" => TagDataType.Float,
            "f64" => TagDataType.Double,
            _ when t.StartsWith("bit") => TagDataType.Boolean,
            _ => TagDataType.String,
        };
    }

    /// <summary>Adds the designed channel to the project and selects it.</summary>
    [RelayCommand]
    private void CreateChannel()
    {
        if (!App.IsEditable) { CreateMessage = "Stop the runtime first: editing is locked while it runs."; return; }

        var (channel, errors) = ActiveTab == 1 ? Text.BuildChannel() : BuildChannel();
        if (channel is null) { CreateMessage = string.Join(" ", errors); return; }

        App.Config.Channels.Add(channel);
        NavHost.Current?.SelectDevice(channel, channel.Devices[0]);
        CreateMessage = $"Created channel '{channel.Name}' ({DriverCatalog.For(channel).DisplayName}) with {channel.Devices[0].Tags.Count} tag(s). Check the port settings, then Start Runtime.";
        App.LogInfo($"Protocol Designer created channel {channel.Name}");
    }

    public void Close()
    {
        Text.Close();
        StopCapture();
        _refresh.Stop();
    }
}
