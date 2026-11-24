using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

/// <summary>Northbound outputs: where tag values are published to (MQTT, HTTP).</summary>
public static class OutputsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var o = api.MapGroup("/outputs").WithTags("Northbound outputs");

        o.MapGet("", (GatewayRuntime rt) => Results.Ok(rt.Config.Northbound.Select(Mask)));

        o.MapGet("/status", (GatewayRuntime rt) => Results.Ok(rt.Outputs.Statuses))
         .WithSummary("State, counters and last error of each running output.");

        o.MapGet("/kinds", () => Results.Ok(NorthboundCatalog.Kinds.Select(k => new
        {
            kind = k.Kind.ToString(), name = k.Name,
            settings = NorthboundCatalog.For(k.Kind).Select(s => new { s.Key, s.Label, s.Default, s.Options, s.Secret }),
        })))
         .WithSummary("The kinds of output and their settings.");

        o.MapGet("/{name}", (string name, GatewayRuntime rt) =>
            Find(rt.Config, name) is { } n ? Results.Ok(Mask(n)) : NotFound($"Output '{name}'"));

        o.MapPut("/{name}", (string name, NorthboundConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            body.Name = name;
            var existing = Find(c, name);
            RestoreSecrets(body, existing);
            // Fill in defaults for settings the caller left out.
            foreach (var d in NorthboundCatalog.For(body.Kind))
                if (!body.Settings.ContainsKey(d.Key)) body.Settings[d.Key] = d.Default;
            if (existing is null) c.Northbound.Add(body); else c.Northbound[c.Northbound.IndexOf(existing)] = body;
            return null;
        }, _ => Results.Ok(Mask(body))))
         .WithSummary("Create or replace an output. Send '********' for a password to keep the stored one.");

        o.MapDelete("/{name}", (string name, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var existing = Find(c, name);
            if (existing is null) return NotFound($"Output '{name}'");
            c.Northbound.Remove(existing);
            return null;
        }, _ => Results.NoContent()));
    }

    private static NorthboundConfig? Find(GatewayConfig c, string name) =>
        c.Northbound.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
}
