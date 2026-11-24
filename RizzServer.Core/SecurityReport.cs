namespace RizzServer.Core;

/// <summary>How the headless service is reachable (null when the project is edited in the desktop app on its own).</summary>
public sealed record ServiceExposure(bool HasCredentials, bool LoopbackOnly, bool UsesHttps, bool SelfSignedCertificate, int AdminKeys, bool AllowInsecureRemote);

public sealed record Finding(string Severity, string Title, string Detail, string Fix);

/// <summary>A plain-language review of how exposed this installation is.</summary>
public static class SecurityReport
{
    public static List<Finding> Check(GatewayConfig config, ServiceExposure? service)
    {
        var f = new List<Finding>();
        var s = config.Server;

        if (s.AllowAnonymous)
            f.Add(new("High", "Anonymous OPC UA access is on", "Any client that can reach the OPC UA port can read every tag (writes are refused without a user).", "Create users, then set AllowAnonymous to false."));
        if (s.AllowNoSecurity)
            f.Add(new("High", "OPC UA accepts unencrypted connections", "The 'None' security mode sends values and passwords in clear text.", "Set AllowNoSecurity to false; clients then use Basic256Sha256 sign & encrypt."));
        if (s.AutoAcceptCertificates)
            f.Add(new("Medium", "Client certificates are accepted without checking", "Any client certificate is trusted, which defeats certificate-based identity.", "Set AutoAcceptCertificates to false and move trusted client certificates into the 'trusted' folder of the pki directory."));
        if (s.Users.Count == 0 && !s.AllowAnonymous)
            f.Add(new("High", "No OPC UA users and anonymous access is off", "Nobody can connect.", "Add at least one user."));
        if (s.Users.Count > 0 && s.Users.All(u => u.Role == UserRole.Admin) && s.Users.Count > 1)
            f.Add(new("Low", "Every OPC UA user is an Admin", "Operators and read-only consumers should have the smaller roles.", "Give read-only consumers the Viewer role and operators the Operator role."));
        foreach (var u in s.Users.Where(u => u.PasswordHash.Split('.') is { Length: 3 } p && int.TryParse(p[0], out var it) && it < 100_000))
            f.Add(new("Medium", $"User '{u.Username}' has a weak password hash", "Stored with fewer than 100000 PBKDF2 iterations.", "Set the password again; it is re-hashed with the current settings."));

        var plain = config.Channels.SelectMany(c => c.Settings.Concat(c.Devices.SelectMany(d => d.Settings)).Select(kv => (c, kv)))
            .Count(x => DriverCatalog.For(x.c).Settings.Any(d => d.Secret && d.Key == x.kv.Key) && x.kv.Value.Length > 0 && !SecretStore.IsProtected(x.kv.Value) && !SecretStore.IsReference(x.kv.Value));
        if (plain > 0)
            f.Add(new("Medium", $"{plain} password(s) are not protected", "They are stored in clear text in the loaded project.", "Save the project; passwords are encrypted on save."));

        if (service is not null)
        {
            if (!service.HasCredentials)
                f.Add(new(service.LoopbackOnly ? "Medium" : "High", "The REST API has no key", "Anyone who can reach the API can change the configuration and write to devices.", "Create a key with: rizzserver-service keys create admin Admin"));
            if (!service.LoopbackOnly && !service.UsesHttps)
                f.Add(new("High", "The API is exposed without HTTPS", "API keys travel in clear text.", "Use an https:// URL or put a TLS-terminating reverse proxy in front."));
            if (service.UsesHttps && service.SelfSignedCertificate)
                f.Add(new("Low", "The API uses a self-signed certificate", "Clients must be told to trust it explicitly.", "Provide a certificate from your CA in CertificatePath."));
            if (service.AdminKeys > 1)
                f.Add(new("Low", "Several admin API keys exist", "Each one can change the whole configuration.", "Give integrations the Viewer or Operator role."));
            if (service.AllowInsecureRemote)
                f.Add(new("High", "AllowInsecureRemote is on", "Plain HTTP is allowed on a non-loopback address.", "Turn it off and use HTTPS."));
        }

        foreach (var n in config.Northbound.Where(n => n.Enabled))
        {
            if (n.Kind == NorthboundKind.Mqtt && !n.SettingBool("UseTls") && n.Setting("Host") is not ("localhost" or "127.0.0.1"))
                f.Add(new("Medium", $"Output '{n.Name}' publishes to a broker without TLS", "Values (and the password, if set) cross the network in clear text.", "Enable UseTls for this output."));
            if (n.Kind == NorthboundKind.Http && n.Setting("Url").StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !n.Setting("Url").Contains("localhost") && !n.Setting("Url").Contains("127.0.0.1"))
                f.Add(new("Medium", $"Output '{n.Name}' posts over plain HTTP", "Values and the authorization header cross the network in clear text.", "Use an https:// URL."));
        }

        return [.. f.OrderBy(x => x.Severity switch { "High" => 0, "Medium" => 1, _ => 2 })];
    }
}
