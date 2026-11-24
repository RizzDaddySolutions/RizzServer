using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RizzServer.Core;
using RizzServer.Server;

namespace RizzServer.Service;

public sealed record Principal(string Name, UserRole Role);

/// <summary>
/// API authentication and authorisation. Keys are compared by hash; a role decides what a key may do;
/// repeated wrong keys from one address are blocked; everything that changes something is written to the audit log.
/// </summary>
public sealed class ApiAuthMiddleware(RequestDelegate next, ServiceSettings settings, AuditLog audit, FailureTracker failures)
{
    public const string PrincipalKey = "rizz.principal";

    public static UserRole RequiredRole(string method, PathString path, QueryString query)
    {
        var p = path.Value ?? "";
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (p.StartsWith("/api/v1/security", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/api/v1/audit", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/v1/redundancy/project", StringComparison.OrdinalIgnoreCase))
            return UserRole.Admin;
        if (read)
            return p.StartsWith("/api/v1/config/export", StringComparison.OrdinalIgnoreCase) && query.Value?.Contains("secrets=true", StringComparison.OrdinalIgnoreCase) == true
                ? UserRole.Admin : UserRole.Viewer;
        if (HttpMethods.IsPut(method) && p.StartsWith("/api/v1/tags/", StringComparison.OrdinalIgnoreCase)) return UserRole.Operator;
        if (HttpMethods.IsPost(method) && p.StartsWith("/api/v1/commissioning/", StringComparison.OrdinalIgnoreCase)) return UserRole.Operator;
        // The validation/diff helpers change nothing.
        if (HttpMethods.IsPost(method) && (p.EndsWith("/config/validate", StringComparison.OrdinalIgnoreCase) || p.EndsWith("/config/diff", StringComparison.OrdinalIgnoreCase)))
            return UserRole.Viewer;
        return UserRole.Admin;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi") || path == "/")
        {
            await next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        Principal principal;

        if (!settings.HasCredentials) principal = new Principal("anonymous", UserRole.Admin);   // auth off: loopback only (enforced at startup)
        else
        {
            if (failures.IsLocked(ip))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "300";
                await context.Response.WriteAsJsonAsync(new { error = "Too many failed attempts. Try again in a few minutes." });
                return;
            }

            var supplied = context.Request.Headers.Authorization.ToString();
            supplied = supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? supplied[7..].Trim() : "";
            if (supplied.Length == 0) supplied = context.Request.Headers["X-Api-Key"].ToString();

            var found = supplied.Length == 0 ? null : Find(supplied);
            if (found is null)
            {
                var locked = failures.Fail(ip);
                if (supplied.Length > 0 || locked) audit.Write(locked ? "auth-blocked" : "auth-failed", "?", ip, $"{context.Request.Method} {path}");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
                return;
            }
            failures.Succeed(ip);
            principal = found;
        }

        var required = RequiredRole(context.Request.Method, path, context.Request.QueryString);
        if (principal.Role < required)
        {
            audit.Write("forbidden", principal.Name, $"{context.Request.Method} {path}", $"needs {required}, has {principal.Role}");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"This key is a {principal.Role} key; the request needs {required}." });
            return;
        }

        context.Items[PrincipalKey] = principal;
        await next(context);

        // What changed something is on the record, with who did it and whether it worked.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method)
            && !path.StartsWithSegments("/api/v1/config/validate") && !path.StartsWithSegments("/api/v1/config/diff"))
            audit.Write("api", principal.Name, $"{context.Request.Method} {path}", $"status {context.Response.StatusCode}");
    }

    private Principal? Find(string key)
    {
        var hash = Encoding.ASCII.GetBytes(ApiKeyEntry.HashOf(key));
        Principal? found = null;
        // Compare every entry (no early exit) so timing doesn't reveal how many keys exist or which matched.
        foreach (var k in settings.ApiKeys)
        {
            var stored = Encoding.ASCII.GetBytes(k.Hash);
            if (stored.Length == hash.Length && CryptographicOperations.FixedTimeEquals(hash, stored)) found = new Principal(k.Name, k.Role);
        }
        if (settings.EnvironmentKey is { } env && CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(ApiKeyEntry.HashOf(env))))
            found ??= new Principal("env", UserRole.Admin);
        return found;
    }
}

public static class TlsCertificate
{
    public static string DefaultPath => Path.Combine(ConfigStore.DataDirectory, "service-cert.pfx");

    /// <summary>The certificate for HTTPS: the configured PFX, or a self-signed one created (and renewed) in the data folder.</summary>
    public static X509Certificate2 Load(ServiceSettings settings, out bool selfSigned)
    {
        selfSigned = false;
        if (settings.CertificatePath.Length > 0)
            return X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, SecretStore.Resolve(settings.CertificatePassword));

        selfSigned = true;
        var path = DefaultPath;
        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, "");
            if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
        }

        using var rsa = RSA.Create(3072);
        var host = Dns.GetHostName();
        var request = new CertificateRequest($"CN={host}, O=RizzServer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddDnsName(host); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        Directory.CreateDirectory(ConfigStore.DataDirectory);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, ""));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return X509CertificateLoader.LoadPkcs12FromFile(path, "");
    }
}
