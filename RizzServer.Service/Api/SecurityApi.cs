using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

public sealed record NewKeyBody(string Name, UserRole? Role);

/// <summary>Security review, API key management and the audit log. Admin keys only.</summary>
public static class SecurityApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Security");

        g.MapGet("/security/report", (GatewayRuntime rt, ServiceSettings settings) => Results.Ok(SecurityReport.Check(rt.Config, settings)))
         .WithSummary("What is exposed and how to fix it, most serious first.");

        g.MapGet("/security/keys", (ServiceSettings settings) => Results.Ok(settings.ApiKeys.Select(k => new { k.Name, role = k.Role.ToString(), k.Created })))
         .WithSummary("API keys (names and roles; the keys themselves are never stored).");

        g.MapPost("/security/keys", (NewKeyBody body, ServiceSettings settings, AuditLog audit, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) return Json(400, new ErrorBody("A key needs a name."));
            string key;
            try { key = settings.AddKey(body.Name.Trim(), body.Role ?? UserRole.Viewer); }
            catch (InvalidOperationException ex) { return Json(409, new ErrorBody(ex.Message)); }
            settings.Save();
            audit.Write("key-created", Who(http), body.Name.Trim(), (body.Role ?? UserRole.Viewer).ToString());
            return Results.Created($"/api/v1/security/keys/{Uri.EscapeDataString(body.Name.Trim())}", new { name = body.Name.Trim(), role = (body.Role ?? UserRole.Viewer).ToString(), key, note = "Store this key now: it cannot be shown again." });
        })
         .WithSummary("Create a key. The key is in the response once; only its hash is kept. Default role: Viewer.");

        g.MapDelete("/security/keys/{name}", (string name, ServiceSettings settings, AuditLog audit, HttpContext http) =>
        {
            var key = settings.ApiKeys.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
            if (key is null) return NotFound($"Key '{name}'");
            if (key.Role == UserRole.Admin && settings.ApiKeys.Count(k => k.Role == UserRole.Admin) == 1)
                return Json(409, new ErrorBody("This is the only admin key. Create another admin key first, or you would lock yourself out."));
            settings.ApiKeys.Remove(key);
            settings.Save();
            audit.Write("key-revoked", Who(http), name, "");
            return Results.NoContent();
        })
         .WithSummary("Revoke a key.");

        g.MapGet("/audit", (AuditLog audit, int? limit) => Results.Ok(audit.Tail(limit ?? 200)))
         .WithSummary("The last security events: logins, API changes, tag writes, key changes.");

        g.MapGet("/audit/verify", (AuditLog audit) =>
        {
            var (lines, bad, problem) = audit.Verify();
            return Results.Ok(new { intact = bad is null, lines, firstBadLine = bad, problem });
        })
         .WithSummary("Re-check the audit log's hash chain.");
    }

    private static string Who(HttpContext http) => (http.Items[ApiAuthMiddleware.PrincipalKey] as Principal)?.Name ?? "?";
}
