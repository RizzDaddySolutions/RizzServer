using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// Connect → poll loop with automatic reconnect. Event-driven protocols (MQTT, serial)
/// push via <see cref="Publish"/> and use <see cref="PollAsync"/> only as a health check.
/// </summary>
public abstract class DriverBase : IDriver
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    protected DriverBase(ConnectionConfig config, ILogger log)
    {
        Config = config;
        Log = log;
    }

    protected ConnectionConfig Config { get; }
    protected ILogger Log { get; }
    protected IEnumerable<TagConfig> Tags => Config.Tags;

    public event Action<TagConfig, object?, TagQuality>? TagUpdated;
    public event Action<ConnectionState, string?>? StateChanged;

    protected abstract Task ConnectAsync(CancellationToken ct);
    protected abstract Task PollAsync(CancellationToken ct);
    protected abstract Task DisconnectAsync();
    public abstract Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default);

    protected void Publish(TagConfig tag, object? value, TagQuality quality = TagQuality.Good) =>
        TagUpdated?.Invoke(tag, value, quality);

    protected void SetState(ConnectionState state, string? message = null) => StateChanged?.Invoke(state, message);

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        SetState(ConnectionState.Stopped);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(50, Config.PollIntervalMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(ConnectionState.Connecting);
                await ConnectAsync(ct);
                SetState(ConnectionState.Connected);
                while (!ct.IsCancellationRequested)
                {
                    await PollAsync(ct);
                    await Task.Delay(poll, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.LogWarning("[{Conn}] {Error}", Config.Name, ex.Message);
                SetState(ConnectionState.Faulted, ex.Message);
                foreach (var t in Tags) Publish(t, null, TagQuality.Bad);
            }
            finally
            {
                try { await DisconnectAsync(); } catch { /* best effort */ }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public virtual async ValueTask DisposeAsync() => await StopAsync();
}
