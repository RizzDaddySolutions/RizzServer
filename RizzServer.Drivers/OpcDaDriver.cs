using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// Classic OPC DA client using the OPC Automation wrapper (OPC.Automation, late-bound COM).
/// All COM calls run on one dedicated STA thread. The wrapper is usually 32-bit only, so a
/// 64-bit process may need the app built for x86 to load it.
/// </summary>
public sealed class OpcDaDriver : DriverBase
{
    private readonly BlockingCollection<Action> _queue = new();
    private Thread? _sta;
    private dynamic? _server;
    private dynamic? _group;
    private readonly Dictionary<TagConfig, dynamic> _items = new();

    public OpcDaDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    private Task<T> OnSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureThread();
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void EnsureThread()
    {
        if (_sta is { IsAlive: true }) return;
        _sta = new Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable()) job();
        })
        { IsBackground = true, Name = $"OPC DA {Config.Name}" };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();
    }

    protected override Task ConnectAsync(CancellationToken ct) => OnSta(() =>
    {
        var type = Type.GetTypeFromProgID("OPC.Automation")
                   ?? throw new InvalidOperationException(
                       "OPC Automation wrapper not registered. Install the OPC Core Components (OPCDAAuto.dll).");
        _server = Activator.CreateInstance(type)!;
        _server.Connect(Config.Setting("ProgId", "Matrikon.OPC.Simulation.1"), Config.Setting("Node"));

        _group = _server.OPCGroups.Add("RizzServer");
        _group.UpdateRate = Math.Max(50, Config.PollIntervalMs);
        _group.IsActive = true;

        _items.Clear();
        var handle = 1;
        foreach (var tag in Tags)
        {
            try { _items[tag] = _group.OPCItems.AddItem(tag.Address, handle++); }
            catch (Exception ex)
            {
                Log.LogWarning("[{Conn}] {Tag}: cannot add item '{Addr}': {Error}", Config.Name, tag.Name, tag.Address, ex.Message);
                Publish(tag, null, TagQuality.Bad);
            }
        }
        return 0;
    });

    protected override Task DisconnectAsync() => OnSta(() =>
    {
        _items.Clear();
        try { _server?.Disconnect(); } catch { /* ignore */ }
        _group = null;
        _server = null;
        return 0;
    });

    protected override Task PollAsync(CancellationToken ct) => OnSta(() =>
    {
        foreach (var (tag, item) in _items)
        {
            object? value = null, quality = null, timestamp = null;
            item.Read(2 /* OPCDevice */, ref value, ref quality, ref timestamp);
            var q = Convert.ToInt32(quality ?? 0);
            var good = (q & 0xC0) == 0xC0;
            Publish(tag, value, good ? TagQuality.Good : (q & 0xC0) == 0x40 ? TagQuality.Uncertain : TagQuality.Bad);
        }
        return 0;
    });

    public override Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default) => OnSta(() =>
    {
        if (!_items.TryGetValue(tag, out var item)) throw new InvalidOperationException("Item not available.");
        item.Write(value);
        return 0;
    });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _queue.CompleteAdding();
    }
}
