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

        var userPolicies = new UserTokenPolicyCollection();
        if (s.AllowAnonymous) userPolicies.Add(new UserTokenPolicy(UserTokenType.Anonymous));
        if (s.Users.Count > 0) userPolicies.Add(new UserTokenPolicy(UserTokenType.UserName));
        if (userPolicies.Count == 0)
            throw new InvalidOperationException("Anonymous access is disabled and no users are defined. Add a user or allow anonymous access.");

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
                UserTokenPolicies = userPolicies,
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

        _server = new RizzStandardServer(_engine, s);
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
        private readonly ServerSettings _settings;

        public RizzStandardServer(GatewayEngine engine, ServerSettings settings)
        {
            _engine = engine;
            _settings = settings;
        }

        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);
            server.SessionManager.ImpersonateUser += OnImpersonateUser;
        }

        private void OnImpersonateUser(ISession session, ImpersonateEventArgs args)
        {
            switch (args.NewIdentity)
            {
                case AnonymousIdentityToken:
                    if (!_settings.AllowAnonymous)
                        throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Anonymous access is disabled.");
                    break;

                case UserNameIdentityToken token:
                    var user = _settings.Users.FirstOrDefault(u =>
                        string.Equals(u.Username, token.UserName, StringComparison.Ordinal));
                    var password = token.DecryptedPassword is { } raw ? System.Text.Encoding.UTF8.GetString(raw) : "";
                    // Verify against a dummy hash when the user is unknown so timing doesn't reveal valid names.
                    var ok = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyHash);
                    if (user is null || !ok)
                        throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Invalid user name or password.");
                    args.Identity = new UserIdentity(token);
                    break;

                default:
                    throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Unsupported identity token.");
            }
        }

        private static readonly string DummyHash = PasswordHasher.Hash("dummy");

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            var nm = new RizzNodeManager(server, configuration, _engine, _settings.NamespaceUri);
            return new MasterNodeManager(server, configuration, null, nm);
        }
    }
}
