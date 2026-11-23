using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>MQTT subscriber/publisher. Address: "topic" or "topic|json.path".</summary>
public sealed class MqttDriver : DriverBase
{
    private IMqttClient? _client;

    public MqttDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    private static (string Topic, string? Path) Split(string address)
    {
        var i = address.IndexOf('|');
        return i < 0 ? (address.Trim(), null) : (address[..i].Trim(), address[(i + 1)..].Trim());
    }

    protected override async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(Config.Setting("Host", "localhost"), Config.SettingInt("Port", 1883))
            // One broker connection per device, so make the client id unique per device.
            .WithClientId($"{Config.Setting("ClientId", "rizzserver")}-{Config.DeviceName}")
            .WithCleanSession();

        var user = Config.Setting("Username");
        if (user.Length > 0) builder.WithCredentials(user, Config.Setting("Password"));
        if (Config.SettingBool("UseTls")) builder.WithTlsOptions(o => o.UseTls());

        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessage;
        await _client.ConnectAsync(builder.Build(), ct);

        foreach (var topic in Tags.Select(t => Split(t.Address).Topic).Where(t => t.Length > 0).Distinct())
            await _client.SubscribeAsync(topic, cancellationToken: ct);
    }

    protected override async Task DisconnectAsync()
    {
        var c = _client;
        _client = null;
        if (c is null) return;
        c.ApplicationMessageReceivedAsync -= OnMessage;
        try { if (c.IsConnected) await c.DisconnectAsync(); } finally { c.Dispose(); }
    }

    protected override Task PollAsync(CancellationToken ct)
    {
        if (_client is not { IsConnected: true }) throw new IOException("MQTT connection lost.");
        return Task.CompletedTask;
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

        foreach (var tag in Tags)
        {
            var (filter, path) = Split(tag.Address);
            if (!TopicMatches(filter, topic)) continue;

            var value = path is null ? payload : ExtractJson(payload, path);
            if (value is not null) Publish(tag, value);
        }
        return Task.CompletedTask;
    }

    private static object? ExtractJson(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            foreach (var part in path.Trim('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (el.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i < el.GetArrayLength())
                    el = el[i];
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(part, out var next))
                    el = next;
                else
                    return null;
            }
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => el.GetRawText(),
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Standard MQTT filter matching with + and #.</summary>
    internal static bool TopicMatches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] != "+" && f[i] != t[i]) return false;
        }
        return f.Length == t.Length;
    }

    public override async Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var client = _client;
        if (client is not { IsConnected: true }) throw new InvalidOperationException("Not connected.");
        var (topic, path) = Split(tag.Address);
        if (path is not null) throw new NotSupportedException("Writing to a JSON path is not supported; use a plain topic.");
        if (topic.Contains('+') || topic.Contains('#')) throw new NotSupportedException("Cannot publish to a wildcard topic.");

        var text = value is bool b ? (b ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        await client.PublishStringAsync(topic, text, cancellationToken: ct);
    }
}
