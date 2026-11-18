using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using RizzServer.Core;

namespace RizzServer.Server;

/// <summary>Starts/stops the OPC UA endpoint that exposes the gateway's tags.</summary>
public sealed class OpcUaHost : IAsyncDisposable
{
    private readonly GatewayEngine _engine;
    private readonly ILogger _log;
    private ApplicationInstance? _app;
    private StandardServer? _server;

    public OpcUaHost(GatewayEngine engine, ILogger log)
    {
        _engine = engine;
        _log = log;
    }

    public bool IsRunning => _server is not null;
    public string EndpointUrl { get; private set; } = "";

    public async Task StartAsync()
    {
        if (_server is not null) return;
        var s = _engine.Config.Server;

        var host = Utils.GetHostName();
        var pki = Path.Combine(ConfigStore.DataDirectory, "pki");
        EndpointUrl = $"opc.tcp://{host}:{s.Port}/RizzServer";

        var config = new ApplicationConfiguration
        {
            ApplicationName = s.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationUri = $"urn:{host}:RizzServer",
            ProductUri = "urn:rizzserver",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN={s.ApplicationName}, O=RizzServer, DC={host}",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = s.AutoAcceptCertificates,
                AddAppCertToTrustedStore = true,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://0.0.0.0:{s.Port}/RizzServer" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,
            },
            TraceConfiguration = new TraceConfiguration(),
        };

        if (s.AllowNoSecurity)
            config.ServerConfiguration.SecurityPolicies.Add(
                new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None });

        await config.Validate(ApplicationType.Server);
        if (s.AutoAcceptCertificates)
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

        _app = new ApplicationInstance(config) { ApplicationName = s.ApplicationName, ApplicationType = ApplicationType.Server };
        if (!await _app.CheckApplicationInstanceCertificates(true, 2048))
            throw new InvalidOperationException("Application certificate could not be created.");

        _server = new RizzStandardServer(_engine, s.NamespaceUri);
        await _app.Start(_server);
        _log.LogInformation("OPC UA server listening on {Url}", EndpointUrl);
    }

    public async Task StopAsync()
    {
        if (_server is null) return;
        try { await _app!.StopAsync(); }
        finally
        {
            _server.Dispose();
            _server = null;
            _app = null;
        }
        _log.LogInformation("OPC UA server stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class RizzStandardServer : StandardServer
    {
        private readonly GatewayEngine _engine;
        private readonly string _ns;

        public RizzStandardServer(GatewayEngine engine, string ns)
        {
            _engine = engine;
            _ns = ns;
        }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            var nm = new RizzNodeManager(server, configuration, _engine, _ns);
            return new MasterNodeManager(server, configuration, null, nm);
        }
    }
}
