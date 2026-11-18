using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using RizzServer.Core;

namespace RizzServer.Drivers;

/// <summary>
/// OPC UA client: subscribes to nodes on another OPC UA server and republishes them.
/// Tag address is the NodeId in string form, e.g. <c>ns=2;s=Pressure</c> or <c>i=2258</c>.
/// </summary>
public sealed class OpcUaClientDriver : DriverBase
{
    private static readonly SemaphoreSlim CertLock = new(1, 1);

    private Session? _session;
    private Subscription? _subscription;
    private volatile bool _lost;
    private readonly Dictionary<TagConfig, NodeId> _nodes = new();

    public OpcUaClientDriver(ConnectionConfig config, ILogger log) : base(config, log) { }

    private async Task<ApplicationConfiguration> CreateConfigurationAsync()
    {
        var pki = Path.Combine(ConfigStore.DataDirectory, "pki-client");
        var host = Utils.GetHostName();
        var config = new ApplicationConfiguration
        {
            ApplicationName = "RizzServer Client",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{host}:RizzServer:Client",
            ProductUri = "urn:rizzserver:client",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN=RizzServer Client, O=RizzServer, DC={host}",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = Config.SettingBool("AutoAcceptCertificates", true),
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration(),
        };

        await config.Validate(ApplicationType.Client);
        if (Config.SettingBool("AutoAcceptCertificates", true))
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

        await CertLock.WaitAsync();
        try
        {
            var app = new ApplicationInstance(config) { ApplicationName = "RizzServer Client", ApplicationType = ApplicationType.Client };
            if (!await app.CheckApplicationInstanceCertificates(false, 2048))
                throw new InvalidOperationException("Client application certificate could not be created.");
        }
        finally { CertLock.Release(); }
        return config;
    }

    protected override async Task ConnectAsync(CancellationToken ct)
    {
        _lost = false;
        var url = Config.Setting("EndpointUrl", "opc.tcp://localhost:4840");
        var secure = !Config.Setting("Security", "None").Equals("None", StringComparison.OrdinalIgnoreCase);

        var config = await CreateConfigurationAsync();
        var description = CoreClientUtils.SelectEndpoint(config, url, secure);
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));

        var user = Config.Setting("Username");
        IUserIdentity identity = user.Length == 0
            ? new UserIdentity()
            : new UserIdentity(user, Encoding.UTF8.GetBytes(Config.Setting("Password")));

        _session = await Session.Create(config, endpoint, false, "RizzServer", 60000, identity, null, ct);
        _session.KeepAlive += (_, e) => { if (ServiceResult.IsBad(e.Status)) _lost = true; };

        _nodes.Clear();
        var interval = Math.Max(50, Config.PollIntervalMs);
        _subscription = new Subscription(_session.DefaultSubscription)
        {
            DisplayName = "RizzServer",
            PublishingEnabled = true,
            PublishingInterval = interval,
        };

        foreach (var tag in Tags)
        {
            try
            {
                var nodeId = NodeId.Parse(tag.Address.Trim());
                _nodes[tag] = nodeId;
                var item = new MonitoredItem(_subscription.DefaultItem)
                {
                    DisplayName = tag.Name,
                    StartNodeId = nodeId,
                    AttributeId = Attributes.Value,
                    SamplingInterval = interval,
                    QueueSize = 1,
                    DiscardOldest = true,
                    Handle = tag,
                };
                item.Notification += OnNotification;
                _subscription.AddItem(item);
            }
            catch (Exception ex) when (ex is ServiceResultException or FormatException)
            {
                Log.LogWarning("[{Conn}] {Tag}: bad NodeId '{Addr}'", Config.Name, tag.Name, tag.Address);
                Publish(tag, null, TagQuality.Bad);
            }
        }

        _session.AddSubscription(_subscription);
        await _subscription.CreateAsync(ct);

        foreach (var item in _subscription.MonitoredItems)
        {
            if (item.Status.Error is { } err && ServiceResult.IsBad(err) && item.Handle is TagConfig tag)
            {
                Log.LogWarning("[{Conn}] {Tag}: {Error}", Config.Name, tag.Name, err.StatusCode);
                Publish(tag, null, TagQuality.Bad);
            }
        }
    }

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        if (item.Handle is not TagConfig tag) return;
        foreach (var dv in item.DequeueValues())
        {
            var quality = StatusCode.IsGood(dv.StatusCode) ? TagQuality.Good
                : StatusCode.IsUncertain(dv.StatusCode) ? TagQuality.Uncertain : TagQuality.Bad;
            Publish(tag, Flatten(dv.Value), quality);
        }
    }

    private static object? Flatten(object? v) => v switch
    {
        null => null,
        Array a => string.Join(",", a.Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))),
        ExtensionObject or Variant => v.ToString(),
        _ => v,
    };

    protected override Task PollAsync(CancellationToken ct)
    {
        if (_lost || _session is not { Connected: true })
            throw new IOException("OPC UA session lost.");
        return Task.CompletedTask;
    }

    protected override async Task DisconnectAsync()
    {
        var s = _session;
        _session = null;
        _subscription = null;
        if (s is null) return;
        try { await s.CloseAsync(); } catch { /* connection may already be gone */ }
        s.Dispose();
    }

    public override async Task WriteAsync(TagConfig tag, object value, CancellationToken ct = default)
    {
        var session = _session is { Connected: true } s ? s : throw new InvalidOperationException("Not connected.");
        if (!_nodes.TryGetValue(tag, out var nodeId)) throw new InvalidOperationException("Invalid NodeId.");

        // Write in the node's current data type, otherwise the server answers BadTypeMismatch.
        var current = await session.ReadValueAsync(nodeId, ct);
        object toWrite = value;
        if (current.Value is IConvertible && value is IConvertible && current.Value.GetType() != value.GetType())
            toWrite = Convert.ChangeType(value, current.Value.GetType(), CultureInfo.InvariantCulture);

        var request = new WriteValueCollection
        {
            new WriteValue { NodeId = nodeId, AttributeId = Attributes.Value, Value = new DataValue(new Variant(toWrite)) },
        };
        var response = await session.WriteAsync(null, request, ct);
        if (StatusCode.IsBad(response.Results[0]))
            throw new InvalidOperationException($"Server rejected the write: {response.Results[0]}");
    }
}
