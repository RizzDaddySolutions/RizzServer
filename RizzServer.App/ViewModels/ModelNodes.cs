using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RizzServer.Core;

namespace RizzServer.App.ViewModels;

/// <summary>What the "Add device from template" dialog returns.</summary>
public sealed record TemplateChoice(DeviceTemplate Template, string DeviceName, Dictionary<string, string> Values);

/// <summary>"Northbound": where tag values are published to (MQTT broker, HTTP endpoint).</summary>
public sealed partial class OutputsNode : NavNode
{
    public OutputsNode() : base("Northbound", Res("FolderIcon"))
    {
        foreach (var o in Outputs) Children.Add(new OutputNode(o));
        Outputs.CollectionChanged += OnChanged;
        App.PropertyChanged += OnAppPropertyChanged;
    }

    public ObservableCollection<NorthboundConfig> Outputs => App.Config.Northbound;

    [ObservableProperty] private NorthboundConfig? _selected;

    private void OnAppPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.IsEditable)) { NewMqttCommand.NotifyCanExecuteChanged(); NewHttpCommand.NotifyCanExecuteChanged(); }
    }

    public override void Detach()
    {
        Outputs.CollectionChanged -= OnChanged;
        App.PropertyChanged -= OnAppPropertyChanged;
        foreach (var c in Children.OfType<OutputNode>()) c.Detach();
        base.Detach();
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var c in Children.OfType<OutputNode>()) c.Detach();
            Children.Clear();
            foreach (var o in Outputs) Children.Add(new OutputNode(o));
            return;
        }
        if (e.OldItems is not null)
            foreach (NorthboundConfig o in e.OldItems)
                if (Children.OfType<OutputNode>().FirstOrDefault(n => n.Config == o) is { } gone) { gone.Detach(); Children.Remove(gone); }
        if (e.NewItems is not null)
            foreach (NorthboundConfig o in e.NewItems) Children.Add(new OutputNode(o));
    }

    public OutputNode? NodeFor(NorthboundConfig c) => Children.OfType<OutputNode>().FirstOrDefault(n => n.Config == c);

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("New MQTT Output…", () => NewAsync(NorthboundKind.Mqtt), () => Editable);
        yield return ItemAsync("New HTTP Output…", () => NewAsync(NorthboundKind.Http), () => Editable);
        yield return Sep();
        foreach (var e in ExpandCollapseItems()) yield return e;
    }

    [RelayCommand(CanExecute = nameof(IsEditable))] private Task NewMqttAsync() => NewAsync(NorthboundKind.Mqtt);
    [RelayCommand(CanExecute = nameof(IsEditable))] private Task NewHttpAsync() => NewAsync(NorthboundKind.Http);
    private bool IsEditable() => Editable;

    [RelayCommand]
    private void Open()
    {
        if (Selected is { } o && NodeFor(o) is { } node) Host?.Select(node);
    }

    public async Task NewAsync(NorthboundKind kind)
    {
        if (Host is null || !Editable) return;
        var existing = Outputs.Select(o => o.Name).ToList();
        var suggestion = kind == NorthboundKind.Mqtt ? "Broker" : "Webhook";
        for (var n = 2; existing.Contains(suggestion, StringComparer.OrdinalIgnoreCase); n++) suggestion = (kind == NorthboundKind.Mqtt ? "Broker" : "Webhook") + n;

        var name = await Host.PromptAsync("New Output", "Output name:", suggestion, v => NameRules.Validate(v, "output", existing));
        if (name is null) return;
        var cfg = NorthboundCatalog.NewDefault(kind, name);
        Outputs.Add(cfg);
        if (NodeFor(cfg) is { } node) Host.Select(node);
    }
}

public sealed partial class OutputNode : NavNode
{
    public OutputNode(NorthboundConfig config) : base(config.Name, Res("ServerIcon"))
    {
        Config = config;
        config.PropertyChanged += OnConfigChanged;
        foreach (var d in NorthboundCatalog.For(config.Kind)) Settings.Add(new SettingItem(config.Settings, d));
    }

    public NorthboundConfig Config { get; }
    public ObservableCollection<SettingItem> Settings { get; } = new();
    public string KindName => NorthboundCatalog.Kinds.First(k => k.Kind == Config.Kind).Name;

    [ObservableProperty] private string _statusText = "Start the runtime to publish.";

    public override void Detach()
    {
        Config.PropertyChanged -= OnConfigChanged;
        base.Detach();
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NorthboundConfig.Name)) Title = Config.Name;
    }

    /// <summary>Called about once a second while this output is selected.</summary>
    public async Task RefreshStatusAsync()
    {
        if (!App.IsRunning) { StatusText = "Start the runtime to publish."; return; }
        try
        {
            var s = (await App.Backend.GetOutputStatusAsync()).FirstOrDefault(x => string.Equals(x.Name, Config.Name, StringComparison.OrdinalIgnoreCase));
            if (s is null) { StatusText = Config.Enabled ? "Not part of the running project (save and restart)." : "Disabled."; return; }
            StatusText = $"{s.State}{(string.IsNullOrEmpty(s.Message) ? "" : ": " + s.Message)}\n{s.Sent} value(s) sent · {s.Failed} failed" +
                         (s.LastSent is { } t ? $" · last {t.ToLocalTime():HH:mm:ss}" : "");
        }
        catch (Exception ex) { StatusText = "Status is not available: " + ex.Message; }
    }

    private string ToggleHeader => Config.Enabled ? "Disable Output" : "Enable Output";

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("Rename…", RenameAsync, () => Editable);
        var toggle = Item(ToggleHeader, () => Config.Enabled = !Config.Enabled, () => Editable);
        Config.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NorthboundConfig.Enabled)) toggle.Header = ToggleHeader; };
        yield return toggle;
        yield return Item("Duplicate Output", Duplicate, () => Editable);
        yield return Sep();
        yield return ItemAsync("Delete Output", DeleteAsync, () => Editable);
    }

    public async Task RenameAsync()
    {
        if (Host is null) return;
        var others = App.Config.Northbound.Where(o => o != Config).Select(o => o.Name);
        var name = await Host.PromptAsync("Rename Output", "Output name:", Config.Name, v => NameRules.Validate(v, "output", others));
        if (name is not null) Config.Name = name;
    }

    private void Duplicate()
    {
        var clone = ConfigStore.Clone(Config);
        var taken = App.Config.Northbound.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = $"{Config.Name} Copy";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{Config.Name} Copy {n}";
        clone.Name = candidate;
        App.Config.Northbound.Add(clone);
    }

    private async Task DeleteAsync()
    {
        if (Host is null || !await Host.ConfirmAsync($"Delete output '{Config.Name}'?")) return;
        App.Config.Northbound.Remove(Config);
        Host.SelectRoot();
    }

}

/// <summary>"Templates": reusable device definitions.</summary>
public sealed partial class TemplatesNode : NavNode
{
    public TemplatesNode() : base("Templates", Res("FolderIcon"))
    {
        foreach (var t in Templates) Children.Add(new TemplateNode(t));
        Templates.CollectionChanged += OnChanged;
        App.PropertyChanged += OnAppPropertyChanged;
    }

    public ObservableCollection<DeviceTemplate> Templates => App.Config.Templates;
    [ObservableProperty] private DeviceTemplate? _selected;

    private void OnAppPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.IsEditable)) NewTemplateCommand.NotifyCanExecuteChanged();
    }

    public override void Detach()
    {
        Templates.CollectionChanged -= OnChanged;
        App.PropertyChanged -= OnAppPropertyChanged;
        foreach (var c in Children.OfType<TemplateNode>()) c.Detach();
        base.Detach();
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var c in Children.OfType<TemplateNode>()) c.Detach();
            Children.Clear();
            foreach (var t in Templates) Children.Add(new TemplateNode(t));
            return;
        }
        if (e.OldItems is not null)
            foreach (DeviceTemplate t in e.OldItems)
                if (Children.OfType<TemplateNode>().FirstOrDefault(n => n.Config == t) is { } gone) { gone.Detach(); Children.Remove(gone); }
        if (e.NewItems is not null)
            foreach (DeviceTemplate t in e.NewItems) Children.Add(new TemplateNode(t));
    }

    public TemplateNode? NodeFor(DeviceTemplate t) => Children.OfType<TemplateNode>().FirstOrDefault(n => n.Config == t);

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("New Template…", NewTemplateAsync, () => Editable);
        yield return Sep();
        foreach (var e in ExpandCollapseItems()) yield return e;
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task NewTemplateAsync()
    {
        if (Host is null) return;
        var existing = Templates.Select(t => t.Name).ToList();
        var name = await Host.PromptAsync("New Template", "Template name:", "Template" + (existing.Count + 1), v => NameRules.Validate(v, "template", existing));
        if (name is null) return;
        var t = new DeviceTemplate { Name = name };
        Templates.Add(t);
        if (NodeFor(t) is { } node) Host.Select(node);
    }

    private bool IsEditable() => Editable;

    [RelayCommand]
    private void Open()
    {
        if (Selected is { } t && NodeFor(t) is { } node) Host?.Select(node);
    }
}

public sealed partial class TemplateNode : NavNode
{
    public TemplateNode(DeviceTemplate config) : base(config.Name, Res("TableIcon"))
    {
        Config = config;
        config.PropertyChanged += OnConfigChanged;
        _parametersText = string.Join("\n", config.Parameters.Select(p => $"{p.Key}={p.Value}"));
        _settingsText = string.Join("\n", config.Settings.Select(p => $"{p.Key}={p.Value}"));
        _selectedDriver = DriverCatalog.Find(config.DriverType, config.PluginId) ?? DriverCatalog.Available[0];
    }

    public DeviceTemplate Config { get; }
    public IReadOnlyList<DriverInfo> Drivers => DriverCatalog.Available;
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<TagAccess> Accesses { get; } = Enum.GetValues<TagAccess>();

    [ObservableProperty] private DriverInfo _selectedDriver;
    [ObservableProperty] private string _parametersText;
    [ObservableProperty] private string _settingsText;
    [ObservableProperty] private string _problems = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RemoveTagCommand))] private TagConfig? _selectedTag;

    public string AddressHint => SelectedDriver.AddressHint;
    public string DeviceSettingKeys => string.Join(", ", SelectedDriver.DeviceSettings.Select(s => s.Key));

    partial void OnSelectedDriverChanged(DriverInfo value)
    {
        Config.DriverType = value.Type;
        Config.PluginId = value.PluginId;
        OnPropertyChanged(nameof(AddressHint));
        OnPropertyChanged(nameof(DeviceSettingKeys));
        Validate();
    }

    partial void OnParametersTextChanged(string value)
    {
        Config.Parameters = ParseLines(value);
        Validate();
    }

    partial void OnSettingsTextChanged(string value)
    {
        Config.Settings = ParseLines(value);
        Validate();
    }

    private static Dictionary<string, string> ParseLines(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0) map[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return map;
    }

    private void Validate()
    {
        var errors = new List<string>();
        TemplateEngine.Validate(Config, App.Config.Templates.Where(t => t != Config).Select(t => t.Name), errors);
        Problems = string.Join("\n", errors);
    }

    public override void Detach()
    {
        Config.PropertyChanged -= OnConfigChanged;
        base.Detach();
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceTemplate.Name)) Title = Config.Name;
    }

    [RelayCommand]
    private void AddTag()
    {
        var n = Config.Tags.Count + 1;
        while (Config.Tags.Any(t => t.Name == $"Tag{n}")) n++;
        var tag = new TagConfig { Name = $"Tag{n}" };
        Config.Tags.Add(tag);
        SelectedTag = tag;
        Validate();
    }

    [RelayCommand(CanExecute = nameof(HasTag))]
    private void RemoveTag()
    {
        if (SelectedTag is not null) Config.Tags.Remove(SelectedTag);
        Validate();
    }

    private bool HasTag() => SelectedTag is not null;

    protected override IEnumerable<MenuEntry> CreateMenu()
    {
        yield return ItemAsync("Rename…", RenameAsync, () => Editable);
        yield return Item("Duplicate Template", Duplicate, () => Editable);
        yield return Sep();
        yield return ItemAsync("Delete Template", DeleteAsync, () => Editable);
    }

    public async Task RenameAsync()
    {
        if (Host is null) return;
        var others = App.Config.Templates.Where(t => t != Config).Select(t => t.Name);
        var name = await Host.PromptAsync("Rename Template", "Template name:", Config.Name, v => NameRules.Validate(v, "template", others));
        if (name is not null) Config.Name = name;
    }

    private void Duplicate()
    {
        var clone = ConfigStore.Clone(Config);
        var taken = App.Config.Templates.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = $"{Config.Name} Copy";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{Config.Name} Copy {n}";
        clone.Name = candidate;
        App.Config.Templates.Add(clone);
    }

    private async Task DeleteAsync()
    {
        if (Host is null) return;
        var users = App.Config.Channels.SelectMany(c => c.Devices).Count(d => string.Equals(d.Template, Config.Name, StringComparison.OrdinalIgnoreCase));
        var text = users > 0 ? $"Delete template '{Config.Name}'? {users} device(s) were created from it; they keep their settings but lose the link." : $"Delete template '{Config.Name}'?";
        if (!await Host.ConfirmAsync(text)) return;
        foreach (var d in App.Config.Channels.SelectMany(c => c.Devices).Where(d => string.Equals(d.Template, Config.Name, StringComparison.OrdinalIgnoreCase)))
        { d.Template = null; d.TemplateParams = null; }
        App.Config.Templates.Remove(Config);
    }
}
