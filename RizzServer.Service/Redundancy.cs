using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RizzServer.Core;
using RizzServer.Server;

namespace RizzServer.Service;

public sealed record RedundancyStatus(
    bool Enabled, string Role, string State, byte ServiceLevel, bool FailBack, string ConfigHash,
    bool PeerReachable, string? PeerState, DateTimeOffset? PeerLastSeen, DateTimeOffset? Since, string? LastTransition);

/// <summary>
/// Keeps a pair of service instances in step. The Primary runs the drivers; the Secondary is a standby: its OPC UA server is
/// up with a low ServiceLevel and the drivers are idle, so it needs no extra connections to devices. When the Primary stops
/// answering for <see cref="RedundancySettings.FailoverAfterMs"/> the Secondary connects its drivers and raises its ServiceLevel;
/// clients that use OPC UA server redundancy (or a failover list) move over. While the Primary is healthy the Secondary copies its project.
/// </summary>
public sealed class RedundancyManager(GatewayRuntime runtime, ServiceSettings service, AuditLog audit, ILogger<RedundancyManager> log) : BackgroundService
{
    private HttpClient? _http;
    private string _httpKey = "";
    private DateTimeOffset _lastUsable = DateTimeOffset.UtcNow;
    private DateTimeOffset? _since, _peerLastSeen;
    private DateTimeOffset _holdUntil;
    private DateTimeOffset? _bothStandbySince;
    private RedundancyStatus? _peer;
    private string? _lastTransition;

    private RedundancySettings Settings => runtime.Config.Redundancy;
    public bool Enabled => Settings.Enabled;

    public string StateName => !Settings.Enabled ? "Disabled" : !runtime.IsRunning ? "Stopped" : runtime.IsStandby ? "Standby" : "Active";

    public RedundancyStatus Status() => new(Settings.Enabled, Settings.Role.ToString(), StateName,
        runtime.Host.IsRunning ? runtime.Host.ServiceLevel : (byte)0, Settings.FailBack, SyncHash(runtime.Config),
        _peer is not null, _peer?.State, _peerLastSeen, _since, _lastTransition);

    /// <summary>What has to be equal on both nodes: users, security flags, channels, templates and outputs. Not the port, name or this section.</summary>
    public static string SyncHash(GatewayConfig config)
    {
        var copy = ConfigStore.Clone(config);
        copy.Redundancy = new RedundancySettings();
        copy.Server.Port = 0;
        copy.Server.ApplicationName = "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigStore.Serialize(copy)))).ToLowerInvariant()[..16];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the host has built the runtime, then run the heartbeat for as long as redundancy is on.
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(Math.Max(200, Settings.HeartbeatMs));
            try
            {
                if (Settings.Enabled) await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Redundancy: {Error}", ex.Message); }

            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Starts the runtime the way the redundancy role needs: as a standby that decides on its first heartbeats.</summary>
    public async Task StartAsync()
    {
        _lastUsable = DateTimeOffset.UtcNow;   // grace period: the peer gets FailoverAfterMs to show up
        _since = DateTimeOffset.UtcNow;
        await runtime.StartAsync(standby: true);
        log.LogInformation("Redundancy: started as {Role}, standby until the peer has been seen (or {Seconds}s passed).", Settings.Role, Settings.FailoverAfterMs / 1000);
    }

    private HttpClient Client()
    {
        var key = SecretStore.Resolve(Settings.PeerApiKey);
        if (_http is not null && _httpKey == Settings.PeerUrl + key + Settings.PeerCertificateThumbprint) return _http;

        _http?.Dispose();
        var handler = new HttpClientHandler();
        var pin = Settings.PeerCertificateThumbprint.Replace(":", "").Replace(" ", "");
        if (pin.Length > 0)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && string.Equals(Convert.ToHexString(SHA256.HashData(cert.RawData)), pin, StringComparison.OrdinalIgnoreCase);
        _http = new HttpClient(handler) { BaseAddress = new Uri(Settings.PeerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMilliseconds(Math.Clamp(Settings.HeartbeatMs, 500, 5000)) };
        if (key.Length > 0) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _httpKey = Settings.PeerUrl + key + Settings.PeerCertificateThumbprint;
        return _http;
    }

    private async Task<RedundancyStatus?> PeerStatusAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Client().GetAsync("api/v1/redundancy", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<RedundancyStatus>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var peer = await PeerStatusAsync(ct);
        _peer = peer;
        if (peer is not null) _peerLastSeen = now;
        if (peer is { State: "Active" or "Standby" }) _lastUsable = now;
        var peerDown = now - _lastUsable > TimeSpan.FromMilliseconds(Settings.FailoverAfterMs);

        if (!runtime.IsRunning) return;   // stopped by an operator: stay out of the way
        var cfg = Settings;

        if (runtime.IsStandby)
        {
            // After a planned switchover this node leaves control to the other one for a while (sync and monitoring continue).
            var held = now < _holdUntil;

            if (peerDown) { if (!held) await PromoteAsync($"the peer has not answered for {cfg.FailoverAfterMs} ms"); return; }
            if (peer is null) return;

            if (peer.State == "Active")
            {
                _bothStandbySince = null;
                if (cfg.Role == RedundancyRole.Secondary && cfg.SyncConfig) await SyncAsync(peer, ct);
                else if (cfg.Role == RedundancyRole.Primary && peer.FailBack && !held) await RequestHandoverAsync(ct);
            }
            else if (peer.State == "Standby")
            {
                _bothStandbySince ??= now;
                var waited = now - _bothStandbySince.Value;
                if (held) return;
                if (cfg.Role == RedundancyRole.Primary) await PromoteAsync("both nodes are standby and this is the primary");
                else if (waited > TimeSpan.FromMilliseconds(cfg.FailoverAfterMs * 2)) await PromoteAsync("both nodes have been standby for too long");
            }
        }
        else if (peer is { State: "Active" })
        {
            // Two active nodes (they could not see each other for a while): one of them steps down.
            if (cfg.Role == RedundancyRole.Secondary && cfg.FailBack) await DemoteAsync("the primary is active again", manual: false);
            else if (cfg.Role == RedundancyRole.Primary && !peer.FailBack) await DemoteAsync("the secondary stays in control (it has fail-back off)", manual: false);
        }
    }

    private async Task PromoteAsync(string reason)
    {
        await runtime.PromoteAsync();
        _since = DateTimeOffset.UtcNow;
        _lastTransition = $"Standby -> Active: {reason}";
        log.LogWarning("Redundancy: this node is now ACTIVE ({Reason})", reason);
        audit.Write("failover", "redundancy", Settings.Role.ToString(), _lastTransition);
    }

    public async Task DemoteAsync(string reason, bool manual)
    {
        if (!runtime.IsRunning || runtime.IsStandby) return;
        await runtime.DemoteAsync();
        _since = DateTimeOffset.UtcNow;
        _lastTransition = $"Active -> Standby: {reason}";
        // After a manual switchover the other node must get the chance to take over before this one reclaims control.
        if (manual) _holdUntil = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(30000, Settings.FailoverAfterMs * 3));
        log.LogWarning("Redundancy: this node is now STANDBY ({Reason})", reason);
        audit.Write("handover", "redundancy", Settings.Role.ToString(), _lastTransition);
    }

    public async Task ManualPromoteAsync()
    {
        _holdUntil = default;
        if (runtime.IsRunning && runtime.IsStandby) await PromoteAsync("promoted by an operator");
    }

    private async Task RequestHandoverAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Client().PostAsync("api/v1/redundancy/demote?handback=true", null, ct);
            if (response.IsSuccessStatusCode) log.LogInformation("Redundancy: asked the secondary to hand control back.");
            else log.LogWarning("Redundancy: the secondary refused the hand-back ({Status}). Does the peer key have the Admin role?", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* next heartbeat */ }
    }

    private async Task SyncAsync(RedundancyStatus peer, CancellationToken ct)
    {
        if (peer.ConfigHash == SyncHash(runtime.Config)) return;
        try
        {
            using var response = await Client().GetAsync("api/v1/redundancy/project", ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Redundancy: could not read the primary's project ({Status}). The peer key needs the Admin role.", (int)response.StatusCode);
                return;
            }
            var incoming = ConfigStore.Deserialize(await response.Content.ReadAsStringAsync(ct));
            // Keep what belongs to this node.
            incoming.Server.Port = runtime.Config.Server.Port;
            incoming.Server.ApplicationName = runtime.Config.Server.ApplicationName;
            incoming.Redundancy = runtime.Config.Redundancy;

            var errors = ConfigValidator.Validate(incoming);
            if (errors.Count > 0) { log.LogWarning("Redundancy: the primary's project is not valid here: {Errors}", string.Join("; ", errors)); return; }

            var changes = ConfigDiff.Compare(runtime.Config, incoming);
            await runtime.ReloadAsync(incoming);
            ConfigStore.Save(runtime.Config);
            _lastTransition = $"Project synchronised from the primary ({changes.Count} difference(s))";
            log.LogInformation("Redundancy: {Message}", _lastTransition);
            audit.Write("config-sync", "redundancy", "project", $"{changes.Count} difference(s) from the primary");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            log.LogWarning("Redundancy: project sync failed: {Error}", ex.Message);
        }
    }
}
