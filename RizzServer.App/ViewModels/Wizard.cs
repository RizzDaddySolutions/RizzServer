using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

public enum WizardMode { NewChannel, NewDevice }

/// <summary>One page of the wizard. The page content is chosen by step type.</summary>
public abstract partial class WizardStep : ObservableObject
{
    protected WizardStep(WizardViewModel wizard) => Wizard = wizard;

    protected WizardViewModel Wizard { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }

    /// <summary>Steps that don't apply (e.g. no device settings for this driver) are skipped.</summary>
    public virtual bool IsSkipped => false;

    /// <summary>Returns an error message, or null when the page is valid.</summary>
    public virtual string? Validate() => null;

    /// <summary>Called each time the page is shown.</summary>
    public virtual void OnEnter() { }

    protected static string? ValidateName(string name, string what, IEnumerable<string> existing) =>
        NameRules.Validate(name, what, existing);
}

public sealed partial class ChannelNameStep : WizardStep
{
    public ChannelNameStep(WizardViewModel w) : base(w) { }

    public override string Title => "Identification";
    public override string Description =>
        "A channel is a communication link (a COM port, a network connection, a broker…). Enter a name for the new channel.";

    [ObservableProperty] private string _name = "";

    public override void OnEnter()
    {
        if (Name.Length > 0) return;
        var n = 1;
        while (Wizard.Config.Channels.Any(c => c.Name == $"Channel{n}")) n++;
        Name = $"Channel{n}";
    }

    public override string? Validate() => ValidateName(Name, "channel", Wizard.Config.Channels.Select(c => c.Name));
}

public sealed partial class DriverStep : WizardStep
{
    public DriverStep(WizardViewModel w) : base(w) => _selected = DriverCatalog.Available[0];

    public override string Title => "Device Driver";
    public override string Description => "Select the protocol this channel uses to talk to its devices.";

    public IReadOnlyList<DriverInfo> Drivers => DriverCatalog.Available;

    [ObservableProperty] private DriverInfo _selected;

    partial void OnSelectedChanged(DriverInfo value) => Wizard.SetDriver(value);

    public override void OnEnter() => Wizard.SetDriver(Selected);
}

public sealed partial class ChannelSettingsStep : WizardStep
{
    private int _builtVersion = -1;

    public ChannelSettingsStep(WizardViewModel w) : base(w) { }

    public override string Title => "Communication Settings";
    public override string Description => "Set how the channel connects. Defaults are filled in for the selected driver.";

    public ObservableCollection<SettingItem> Settings { get; } = new();
    [ObservableProperty] private string _portsHint = "";

    public override bool IsSkipped => !DriverCatalog.For(Wizard.Channel).ChannelSettings.Any();

    public override void OnEnter()
    {
        if (_builtVersion == Wizard.DriverVersion) return;
        _builtVersion = Wizard.DriverVersion;
        Settings.Clear();
        foreach (var d in DriverCatalog.For(Wizard.Channel).ChannelSettings)
            Settings.Add(new SettingItem(Wizard.Channel.Settings, d));

        PortsHint = Settings.Any(s => s.Descriptor.Key == "PortName") ? DetectPorts() : "";
    }

    private static string DetectPorts()
    {
        // The ports that matter are the service machine's, which can't be listed from here.
        if (AppState.Instance.IsRemote) return "Enter the port name as the service machine knows it (for example COM3 or /dev/ttyUSB0).";
        try
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            return ports.Length == 0 ? "No serial ports detected." : "Detected ports: " + string.Join(", ", ports);
        }
        catch { return ""; }
    }
}

public sealed partial class DeviceNameStep : WizardStep
{
    public DeviceNameStep(WizardViewModel w) : base(w) { }

    public override string Title => "Device Identification";
    public override string Description =>
        "A device is one endpoint on the channel, such as a PLC, an instrument or a topic group. Enter a name for the new device.";

    [ObservableProperty] private string _name = "";

    public override void OnEnter()
    {
        if (Name.Length > 0) return;
        var n = Wizard.Channel.Devices.Count + 1;
        while (Wizard.Channel.Devices.Any(d => d.Name == $"Device{n}")) n++;
        Name = $"Device{n}";
    }

    public override string? Validate() => ValidateName(Name, "device", Wizard.Channel.Devices.Select(d => d.Name));
}

public sealed partial class DeviceSettingsStep : WizardStep
{
    private int _builtVersion = -1;

    public DeviceSettingsStep(WizardViewModel w) : base(w) { }

    public override string Title => "Device Settings";
    public override string Description => "Settings specific to this device (unit ID, framing, byte order…).";

    public ObservableCollection<SettingItem> Settings { get; } = new();

    public override bool IsSkipped => !DriverCatalog.For(Wizard.Channel).DeviceSettings.Any();

    public override void OnEnter()
    {
        if (_builtVersion == Wizard.DriverVersion) return;
        _builtVersion = Wizard.DriverVersion;
        Settings.Clear();
        foreach (var d in DriverCatalog.For(Wizard.Channel).DeviceSettings)
            Settings.Add(new SettingItem(Wizard.Device.Settings, d));
    }
}

public sealed partial class TimingStep : WizardStep
{
    public TimingStep(WizardViewModel w) : base(w) { }

    public override string Title => "Timing";
    public override string Description =>
        "How often values are read from the device (the scan rate). Event-driven drivers (MQTT, OPC UA, serial streams) use it as their update interval.";

    [ObservableProperty] private string _scanRate = "1000";

    public override string? Validate() =>
        int.TryParse(ScanRate, out var ms) && ms >= 10 && ms <= 3_600_000
            ? null
            : "Enter a scan rate between 10 and 3600000 ms.";
}

public sealed partial class SummaryStep : WizardStep
{
    public SummaryStep(WizardViewModel w) : base(w) { }

    public override string Title => "Summary";
    public override string Description => "Review your choices and click Finish to create the device.";

    [ObservableProperty] private string _text = "";

    public override void OnEnter() => Text = Wizard.BuildSummary();
}

/// <summary>
/// Kepware-style wizard: New Channel (identification, driver, communications) followed by a device
/// (identification, settings, timing) — or just the device part for an existing channel.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly ChannelNameStep _channelName;
    private readonly DeviceNameStep _deviceName;
    private readonly TimingStep _timing;

    private WizardViewModel(GatewayConfig config, WizardMode mode, ChannelConfig channel)
    {
        Config = config;
        Mode = mode;
        Channel = channel;
        Device = new DeviceConfig();

        _channelName = new ChannelNameStep(this);
        _deviceName = new DeviceNameStep(this);
        _timing = new TimingStep(this);
        var steps = new List<WizardStep>();
        if (mode == WizardMode.NewChannel)
            steps.AddRange([_channelName, new DriverStep(this), new ChannelSettingsStep(this)]);
        steps.AddRange([_deviceName, new DeviceSettingsStep(this), _timing, new SummaryStep(this)]);
        Steps = steps;

        _current = Steps[0];
        _current.OnEnter();
    }

    public static WizardViewModel ForNewChannel(GatewayConfig config) =>
        new(config, WizardMode.NewChannel, new ChannelConfig());

    public static WizardViewModel ForNewDevice(GatewayConfig config, ChannelConfig channel) =>
        new(config, WizardMode.NewDevice, channel);

    public GatewayConfig Config { get; }
    public WizardMode Mode { get; }
    /// <summary>The channel being created (draft) or the existing channel receiving the device.</summary>
    public ChannelConfig Channel { get; }
    public DeviceConfig Device { get; }
    public IReadOnlyList<WizardStep> Steps { get; }

    /// <summary>Incremented whenever the selected driver changes.</summary>
    internal int DriverVersion { get; private set; }

    public string Heading => Mode == WizardMode.NewChannel ? "New Channel Wizard" : $"New Device Wizard — {Channel.Name}";

    [ObservableProperty] private WizardStep _current;
    [ObservableProperty] private string _error = "";

    public bool HasError => Error.Length > 0;
    public bool CanGoBack => PreviousIndex() >= 0;
    public bool IsLast => NextIndex() < 0;

    public string StepText
    {
        get
        {
            var active = Steps.Where(s => !s.IsSkipped).ToList();
            return $"Step {active.IndexOf(Current) + 1} of {active.Count}";
        }
    }

    public event Action<bool>? CloseRequested;

    /// <summary>Called by the driver step; the draft channel's settings belong to one driver only.</summary>
    internal void SetDriver(DriverInfo info)
    {
        var type = info.Type;
        if (Mode != WizardMode.NewChannel || (Channel.DriverType == type && string.Equals(Channel.PluginId, info.PluginId, StringComparison.OrdinalIgnoreCase))) return;
        Channel.PluginId = info.PluginId;
        DriverVersion++; // settings pages rebuild their rows on next entry, even if the user switches back to an earlier driver
        Channel.DriverType = type;
        Channel.Settings.Clear();
        Device.Settings.Clear();
        RefreshNav();
    }

    private int Index => Steps.ToList().IndexOf(Current);

    private int NextIndex()
    {
        for (var i = Index + 1; i < Steps.Count; i++) if (!Steps[i].IsSkipped) return i;
        return -1;
    }

    private int PreviousIndex()
    {
        for (var i = Index - 1; i >= 0; i--) if (!Steps[i].IsSkipped) return i;
        return -1;
    }

    private void RefreshNav()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(StepText));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnCurrentChanged(WizardStep value)
    {
        Error = "";
        RefreshNav();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (Current.Validate() is { } error) { Error = error; return; }
        var next = NextIndex();
        if (next < 0) return;
        Steps[next].OnEnter();
        Current = Steps[next];
    }

    private bool CanNext() => !IsLast;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        var prev = PreviousIndex();
        if (prev < 0) return;
        Steps[prev].OnEnter();
        Current = Steps[prev];
    }

    [RelayCommand]
    private void Finish()
    {
        // Revalidate every page so nothing invalid slips through by going back and editing.
        foreach (var step in Steps.Where(s => !s.IsSkipped))
            if (step.Validate() is { } error)
            {
                step.OnEnter();
                Current = step;
                Error = error;
                return;
            }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    /// <summary>Creates the channel/device in the project. Call after the wizard closed with Finish.</summary>
    public (ChannelConfig Channel, DeviceConfig Device) Commit()
    {
        Device.Name = _deviceName.Name.Trim();
        Device.ScanRateMs = int.Parse(_timing.ScanRate);

        if (Mode == WizardMode.NewChannel)
        {
            // The draft is complete before it joins the project, so no change handlers fire mid-build.
            Channel.Name = _channelName.Name.Trim();
            Channel.Devices.Add(Device);
            Config.Channels.Add(Channel);
        }
        else Channel.Devices.Add(Device);
        return (Channel, Device);
    }

    internal string BuildSummary()
    {
        var info = DriverCatalog.For(Channel);
        var lines = new List<string>();
        if (Mode == WizardMode.NewChannel)
        {
            lines.Add($"Channel:  {_channelName.Name.Trim()}");
            lines.Add($"Driver:   {info.DisplayName}");
            foreach (var d in info.ChannelSettings.Where(d => !d.Secret))
                lines.Add($"   {d.Label}: {Channel.Settings.GetValueOrDefault(d.Key, d.Default)}");
            lines.Add("");
        }
        else lines.Add($"Channel:  {Channel.Name} ({info.DisplayName})");

        lines.Add($"Device:   {_deviceName.Name.Trim()}");
        foreach (var d in info.DeviceSettings)
            lines.Add($"   {d.Label}: {Device.Settings.GetValueOrDefault(d.Key, d.Default)}");
        lines.Add($"   Scan rate: {_timing.ScanRate} ms");
        lines.Add("");
        lines.Add("Tags can be added to the device after the wizard closes.");
        return string.Join(Environment.NewLine, lines);
    }
}
