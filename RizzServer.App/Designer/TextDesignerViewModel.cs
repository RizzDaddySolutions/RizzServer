using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;
using RizzServer.Drivers;

namespace RizzServer.App.Designer;

/// <summary>One line of the terminal: sent by us (→) or received (←).</summary>
public sealed class TextEntry
{
    public required string Time { get; init; }
    public required bool IsTx { get; init; }
    public required string Text { get; init; }    // control characters shown escaped
    public required string Raw { get; init; }
    public string Direction => IsTx ? "→" : "←";
}

/// <summary>All replies seen for one command (or the unsolicited stream, with an empty command).</summary>
public sealed partial class ExchangeInfo : ObservableObject
{
    public required string Command { get; init; }          // escaped, without terminator
    public List<string> Replies { get; } = new();
    [ObservableProperty] private int _count;
    [ObservableProperty] private string _last = "";

    public string Title => Command.Length == 0 ? "(lines sent by the device)" : Command;

    public void Add(string reply)
    {
        Replies.Add(reply);
        if (Replies.Count > 1000) Replies.RemoveAt(0);
        Count = Replies.Count;
        Last = reply;
    }
}

public sealed class TokenRow
{
    public required int Index { get; init; }
    public required string Text { get; init; }
    public required string Number { get; init; }
}

/// <summary>A value of a text protocol: which command asks for it (if any), how to pick it out of the reply, and its type.</summary>
public sealed partial class TextField : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _parse = "0";
    [ObservableProperty] private TagDataType _dataType = TagDataType.Double;
    [ObservableProperty] private string _value = "";
}

/// <summary>
/// Reverse-engineering line-based serial protocols: a terminal that sends commands and shows the replies, and
/// analysis that finds the line ending, the separator and the values — for devices that stream lines on their
/// own (RS-232 text driver) and for devices that answer commands (command/response driver).
/// </summary>
public sealed partial class TextDesignerViewModel : ObservableObject
{
    private const int MaxEntries = 2000;

    private readonly ProtocolDesignerViewModel _parent;
    private readonly StringBuilder _rx = new();
    private readonly DispatcherTimer _pollTimer = new();
    private ExchangeInfo? _pending;           // the command we are waiting for replies to
    private DateTime _pendingTime;
    private bool _terminatorTouched;
    private bool _separatorTouched;
    private readonly ExchangeInfo _stream = new() { Command = "" };

    public TextDesignerViewModel(ProtocolDesignerViewModel parent)
    {
        _parent = parent;
        _pollTimer.Tick += (_, _) => SendCore();
    }

    // ---- line structure ----
    [ObservableProperty] private string _terminator = "";        // escaped notation; empty = not known yet
    [ObservableProperty] private string _separator = "";
    [ObservableProperty] private string _summary = "Send a command, or capture a device that talks on its own. Line ending and separator are detected from the data.";

    partial void OnTerminatorChanged(string value) { if (!_settingTerminator) _terminatorTouched = true; }
    partial void OnSeparatorChanged(string value) { if (!_settingSeparator) _separatorTouched = true; RefreshTokens(); UpdateFieldValues(); }
    private bool _settingTerminator, _settingSeparator;

    private string TerminatorChars => TextAnalysis.Unescape(Terminator);
    private string SeparatorChars => TextAnalysis.Unescape(Separator);

    // ---- terminal ----
    public ObservableCollection<TextEntry> Entries { get; } = new();
    public IReadOnlyList<string> CommandTerminators { get; } = ["\\r", "\\n", "\\r\\n", "(none)"];

    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _commandTerminator = "\\r";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PollButtonText))] private bool _isPolling;
    [ObservableProperty] private string _pollMs = "500";
    [ObservableProperty] private string _partial = "";
    [ObservableProperty] private TextEntry? _selectedEntry;

    public string PollButtonText => IsPolling ? "Stop polling" : "Poll";

    partial void OnSelectedEntryChanged(TextEntry? value) => RefreshTokens();

    [RelayCommand]
    private void Send() => SendCore();

    private void SendCore()
    {
        var typed = CommandText;
        if (typed.Length == 0) return;
        var terminator = CommandTerminator == "(none)" ? "" : TextAnalysis.Unescape(CommandTerminator);
        var text = TextAnalysis.Unescape(typed) + terminator;

        if (!_parent.TrySend(Encoding.Latin1.GetBytes(text)))
        {
            Summary = "Start the capture first: commands are written to the open serial port.";
            StopPolling();
            return;
        }

        var command = TextAnalysis.Escape(TextAnalysis.Unescape(typed));
        var exchange = Exchanges.FirstOrDefault(e => e.Command == command);
        if (exchange is null)
        {
            exchange = new ExchangeInfo { Command = command };
            Exchanges.Add(exchange);
            SelectedExchange ??= exchange;
        }
        _pending = exchange;
        _pendingTime = DateTime.Now;
        Add(new TextEntry { Time = DateTime.Now.ToString("HH:mm:ss.fff"), IsTx = true, Text = TextAnalysis.Escape(text), Raw = text });
    }

    [RelayCommand]
    private void TogglePolling()
    {
        if (IsPolling) { StopPolling(); return; }
        if (CommandText.Length == 0) { Summary = "Type a command to poll."; return; }
        _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(int.TryParse(PollMs, out var ms) ? ms : 500, 50, 60000));
        IsPolling = true;
        SendCore();
        if (IsPolling) _pollTimer.Start();
    }

    public void StopPolling()
    {
        _pollTimer.Stop();
        IsPolling = false;
    }

    // ---- data in ----

    /// <summary>Called for every chunk the capture receives (any thread).</summary>
    public void OnReceived(byte[] data, DateTime time) => Dispatcher.UIThread.Post(() => Receive(data, time));

    /// <summary>Resets everything (new capture or file).</summary>
    public void Reset()
    {
        StopPolling();
        _rx.Clear();
        _pending = null;
        Entries.Clear();
        Exchanges.Clear();
        _stream.Replies.Clear();
        _stream.Count = 0;
        Tokens.Clear();
        Partial = "";
        if (!_terminatorTouched) { _settingTerminator = true; Terminator = ""; _settingTerminator = false; }
        if (!_separatorTouched) { _settingSeparator = true; Separator = ""; _settingSeparator = false; }
    }

    private void Receive(byte[] data, DateTime time)
    {
        _rx.Append(TextAnalysis.Decode(data));

        // Work out the line ending as soon as it repeats; until then keep the text as the partial line.
        if (Terminator.Length == 0 && !_terminatorTouched)
        {
            var detected = TextAnalysis.DetectTerminator(Encoding.Latin1.GetBytes(_rx.ToString()));
            if (detected.Length > 0)
            {
                _settingTerminator = true;
                Terminator = TextAnalysis.Escape(detected);
                _settingTerminator = false;
            }
        }

        var terminator = TerminatorChars;
        if (terminator.Length == 0) { Partial = TextAnalysis.Escape(_rx.ToString()); return; }

        var lines = TextAnalysis.SplitLines(_rx.ToString(), terminator, out var rest);
        _rx.Clear().Append(rest);
        Partial = TextAnalysis.Escape(rest);

        ExchangeInfo? last = null;
        foreach (var line in lines)
        {
            Add(new TextEntry { Time = time.ToString("HH:mm:ss.fff"), IsTx = false, Text = TextAnalysis.Escape(line), Raw = line });

            // Lines that arrive soon after a command are its reply; anything else is the device talking on its own.
            var target = _pending is not null && time - _pendingTime < TimeSpan.FromSeconds(3) ? _pending : _stream;
            if (target == _stream && !Exchanges.Contains(_stream)) Exchanges.Insert(0, _stream);
            target.Add(line);
            last = target;
            SelectedExchange ??= target;
        }
        if (last is not null) AfterNewLines(last);
    }

    private void Add(TextEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    private void AfterNewLines(ExchangeInfo received)
    {
        // The first few lines are enough to find the separator.
        if (!_separatorTouched && Separator.Length == 0 && received is { Replies.Count: >= 5 } ex)
        {
            var sep = TextAnalysis.DetectSeparator(ex.Replies);
            if (sep.Length > 0)
            {
                _settingSeparator = true;
                Separator = TextAnalysis.Escape(sep);
                _settingSeparator = false;
            }
        }
        UpdateFieldValues();
        if (SelectedEntry is null) RefreshTokens();
    }

    /// <summary>Loads text that wasn't captured live (a file or pasted text).</summary>
    public void LoadText(byte[] bytes)
    {
        Reset();
        Receive(bytes, DateTime.Now);
    }

    // ---- exchanges and tokens ----
    public ObservableCollection<ExchangeInfo> Exchanges { get; } = new();
    [ObservableProperty] private ExchangeInfo? _selectedExchange;
    public ObservableCollection<TokenRow> Tokens { get; } = new();
    [ObservableProperty] private TokenRow? _selectedToken;

    partial void OnSelectedExchangeChanged(ExchangeInfo? value) => RefreshTokens();

    /// <summary>The exchange being worked on: the selected one, or the first when the list has lost its selection.</summary>
    private ExchangeInfo? Current => SelectedExchange ?? Exchanges.FirstOrDefault();

    private string? CurrentLine => SelectedEntry is { IsTx: false } e ? e.Raw : Current?.Replies.LastOrDefault();

    private void RefreshTokens()
    {
        Tokens.Clear();
        if (CurrentLine is not { } line) return;
        var sep = SeparatorChars;
        var parts = sep.Length == 0 ? [line] : line.Split(sep);
        for (var i = 0; i < parts.Length; i++)
            Tokens.Add(new TokenRow
            {
                Index = i,
                Text = parts[i].Trim(),
                Number = TextAnalysis.TryNumber(parts[i], out var v) ? v.ToString("0.#####", CultureInfo.InvariantCulture) : "",
            });
    }

    [RelayCommand]
    private void Detect()
    {
        if (Current is not { Replies.Count: > 0 } ex) { Summary = "Nothing received yet."; return; }
        var sep = TextAnalysis.DetectSeparator(ex.Replies);
        _settingSeparator = true;
        Separator = TextAnalysis.Escape(sep);
        _settingSeparator = false;
        _separatorTouched = false;
        RefreshTokens();
        Summary = sep.Length == 0 ? "No consistent separator: values will be picked with patterns." : $"Separator {(sep == " " ? "space" : "'" + sep + "'")} splits the lines into the same parts.";
    }

    // ---- fields ----
    public ObservableCollection<TextField> Fields { get; } = new();
    [ObservableProperty] private TextField? _selectedField;
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();

    public TextField AddField(string name, string command, string parse, TagDataType type)
    {
        var field = new TextField { Name = Unique(name), Command = command, Parse = parse, DataType = type };
        field.PropertyChanged += (_, _) => UpdateFieldValues();
        Fields.Add(field);
        SelectedField = field;
        UpdateFieldValues();
        return field;
    }

    private string Unique(string name)
    {
        var candidate = name.Trim().Length == 0 ? "Value" : name.Trim();
        var n = 2;
        var baseName = candidate;
        while (Fields.Any(f => f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{baseName}{n++}";
        return candidate;
    }

    /// <summary>The selected token as a field: by column when it is the whole value, by pattern when the number is mixed with text.</summary>
    [RelayCommand]
    private void AddFieldFromToken()
    {
        if (SelectedToken is not { } token || Current is not { } ex) return;
        var command = ex.Command;

        if (TextAnalysis.TryNumber(token.Text, out _) && SeparatorChars.Length > 0)
        {
            AddField($"Value{token.Index}", command, token.Index.ToString(CultureInfo.InvariantCulture),
                TextAnalysis.GuessType(ex.Replies.Select(r => TextExtract.Extract(r, token.Index.ToString(CultureInfo.InvariantCulture), SeparatorChars) ?? "")));
            return;
        }

        var numbersBefore = Tokens.Where(t => t.Index < token.Index).Sum(t => TextAnalysis.CountNumbers(t.Text));
        if (TextAnalysis.CountNumbers(token.Text) > 0 && TextAnalysis.SuggestNumberField(ex.Replies, numbersBefore) is { } f)
        {
            AddField(f.Name, command, f.Spec, f.DataType);
            return;
        }

        AddField($"Text{token.Index}", command, token.Index.ToString(CultureInfo.InvariantCulture), TagDataType.String);
    }

    /// <summary>For replies that are just the value ("23.5"): the whole line.</summary>
    [RelayCommand]
    private void AddWholeReply()
    {
        if (Current is not { } ex) return;
        var type = TextAnalysis.GuessType(ex.Replies.TakeLast(50).Select(r => r.Trim()));
        AddField(ex.Command.Length == 0 ? "Value" : Sanitize(ex.Command), ex.Command, "line", type);
    }

    private static string Sanitize(string command)
    {
        var letters = new string(command.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length == 0 ? "Value" : letters;
    }

    /// <summary>Offers a field for every varying value in the replies of the selected command.</summary>
    [RelayCommand]
    private void SuggestFields()
    {
        if (Current is not { } ex || ex.Replies.Count < 3) { Summary = "Need at least three lines of the same kind (poll the command a few times)."; return; }

        var suggestions = TextAnalysis.SuggestFields(ex.Replies, SeparatorChars);
        if (suggestions.Count == 0)
        {
            Summary = "No values change between lines. Try polling longer, or pick a token yourself.";
            return;
        }

        var prefix = ex.Command.Length == 0 ? "" : Sanitize(ex.Command) + "_";
        foreach (var s in suggestions)
            if (!Fields.Any(f => f.Command == ex.Command && f.Parse == s.Spec))
                AddField(suggestions.Count == 1 && prefix.Length > 0 ? prefix.TrimEnd('_') : prefix + s.Name, ex.Command, s.Spec, s.DataType);
        Summary = $"Suggested {suggestions.Count} field(s). Rename them and delete what you don't need.";
    }

    [RelayCommand]
    private void RemoveField()
    {
        if (SelectedField is { } f) Fields.Remove(f);
    }

    private void UpdateFieldValues()
    {
        foreach (var f in Fields)
        {
            var source = f.Command.Length == 0 ? _stream : Exchanges.FirstOrDefault(e => e.Command == f.Command);
            if (source is null || source.Replies.Count == 0) { f.Value = ""; continue; }
            f.Value = TextExtract.Extract(source.Replies[^1], f.Parse, SeparatorChars) ?? "(no match)";
        }
    }

    // ---- output ----

    /// <summary>The channel the design describes: the stream driver when the device talks on its own, command/response otherwise.</summary>
    public (ChannelConfig? Channel, List<string> Errors) BuildChannel()
    {
        var errors = new List<string>();
        if (Fields.Count == 0) errors.Add("Add at least one field first.");

        var commandBased = Fields.Any(f => f.Command.Length > 0);
        if (commandBased && Fields.Any(f => f.Command.Length == 0))
            errors.Add("Mixing fields with and without a command isn't possible: use one channel for the device's own lines and another for polled commands.");

        var channel = new ChannelConfig
        {
            Name = _parent.ChannelName.Trim(),
            DriverType = commandBased ? DriverType.Rs232Poll : DriverType.Serial,
        };
        channel.Settings["PortName"] = _parent.PortName;
        channel.Settings["BaudRate"] = _parent.BaudRate;
        channel.Settings["DataBits"] = _parent.DataBits;
        channel.Settings["Parity"] = _parent.Parity;
        channel.Settings["StopBits"] = _parent.StopBits;

        var device = new DeviceConfig { Name = _parent.DeviceName.Trim(), ScanRateMs = 1000 };
        device.Settings["Terminator"] = Terminator.Length > 0 ? Terminator : "\\r\\n";
        device.Settings["Separator"] = Separator.Length > 0 ? Separator : ",";
        if (commandBased)
        {
            device.Settings["CommandTerminator"] = CommandTerminator == "(none)" ? "" : CommandTerminator;
            device.Settings["TimeoutMs"] = "1000";
            device.Settings["InterCommandDelayMs"] = "50";
        }

        foreach (var f in Fields)
        {
            if (f.Parse.Contains('|') && commandBased)
                errors.Add($"'{f.Name}': a pattern can't contain '|' in a command address (the driver splits on it).");
            device.Tags.Add(new TagConfig
            {
                Name = f.Name.Trim(),
                Address = commandBased ? $"{f.Command}|{f.Parse}" : f.Parse,
                DataType = f.DataType,
                Access = TagAccess.ReadOnly,
            });
        }
        channel.Devices.Add(device);

        ConfigValidator.ValidateChannel(channel, AppState.Instance.Config.Channels.Select(c => c.Name), errors);
        return (errors.Count == 0 ? channel : null, errors);
    }

    public void Close() => StopPolling();
}
