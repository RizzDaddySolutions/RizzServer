using System.Text;
using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

public sealed record TemplateInstanceBody(string Template, string Name, Dictionary<string, string>? Parameters);

/// <summary>Configuration as code (export, validate, diff, apply a whole project file) and device templates.</summary>
public static class ProjectApi
{
    /// <summary>Reads a project from a request body: format from ?format=, the content type, or by looking at the first character.</summary>
    public static async Task<(GatewayConfig? Config, IResult? Failure)> ReadProject(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return (null, Json(400, new ErrorBody("The request body is empty.")));

        var format = request.Query["format"].ToString().ToLowerInvariant() switch
        {
            "yaml" or "yml" => ConfigStore.Format.Yaml,
            "json" => ConfigStore.Format.Json,
            _ => request.ContentType?.Contains("yaml", StringComparison.OrdinalIgnoreCase) == true || !text.TrimStart().StartsWith('{')
                ? ConfigStore.Format.Yaml : ConfigStore.Format.Json,
        };
        try { return (ConfigStore.Deserialize(text, format), null); }
        catch (FormatException ex) { return (null, Json(400, new ErrorBody("The project file could not be read.", [ex.Message]))); }
    }

    public static void Map(RouteGroupBuilder api)
    {
        var code = api.MapGroup("/config").WithTags("Configuration as code");

        code.MapGet("/export", (GatewayRuntime rt, string? format, bool? secrets) =>
        {
            var fmt = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "yml", StringComparison.OrdinalIgnoreCase)
                ? ConfigStore.Format.Yaml : ConfigStore.Format.Json;
            // Secrets become ${env:…} references unless the caller explicitly wants them (then they stay encrypted).
            var text = ConfigStore.ExportText(rt.Config, fmt, secrets == true, out _);
            return Results.Text(text, fmt == ConfigStore.Format.Yaml ? "application/yaml" : "application/json", Encoding.UTF8);
        })
        .WithSummary("The project as a JSON or YAML file for version control. Secrets become ${env:NAME} references (secrets=true keeps them, encrypted).");

        code.MapGet("/schema", () => Results.Text(ConfigStore.Schema(), "application/schema+json"))
            .WithSummary("JSON Schema of the project file.");

        code.MapPost("/validate", async (HttpRequest request) =>
        {
            var (config, failure) = await ReadProject(request);
            if (failure is not null) return failure;
            var errors = ConfigValidator.Validate(config!);
            return Results.Ok(new { valid = errors.Count == 0, errors });
        })
        .WithSummary("Check a project file (body, JSON or YAML) without applying it.");

        code.MapPost("/diff", async (HttpRequest request, GatewayRuntime rt) =>
        {
            var (config, failure) = await ReadProject(request);
            if (failure is not null) return failure;
            return Results.Ok(ConfigDiff.Compare(rt.Config, config!).Select(d => new { kind = d.Kind.ToString(), d.Path, d.Old, d.New }));
        })
        .WithSummary("What applying this project file would change, by name (secrets are never shown).");

        code.MapPost("/apply", async (HttpRequest request, GatewayRuntime rt, ConfigEditor editor, bool? restart) =>
        {
            var (incoming, failure) = await ReadProject(request);
            if (failure is not null) return failure;

            var wasRunning = rt.IsRunning;
            if (wasRunning && restart == true) await rt.StopAsync();

            List<DiffEntry> changes = [];
            var result = await editor.EditAsync(c =>
            {
                changes = ConfigDiff.Compare(c, incoming!);
                ConfigStore.CopyInto(c, incoming!);
                return null;
            }, _ => Results.Ok(new { applied = true, restarted = wasRunning && restart == true, changes = changes.Select(d => d.ToString()) }));

            if (wasRunning && restart == true)
            {
                try { await rt.StartAsync(); } catch (Exception ex) { return Json(500, new ErrorBody("Applied, but the runtime did not start: " + ex.Message)); }
            }
            return result;
        })
        .WithSummary("Replace the whole project with a file. restart=true stops and starts the runtime around the change.");

        // ---- templates ----
        var tpl = api.MapGroup("/templates").WithTags("Templates");

        tpl.MapGet("", (GatewayRuntime rt) => Results.Ok(rt.Config.Templates));

        tpl.MapGet("/{name}", (string name, GatewayRuntime rt) =>
            Find(rt.Config, name) is { } t ? Results.Ok(t) : NotFound($"Template '{name}'"));

        tpl.MapPut("/{name}", (string name, DeviceTemplate body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            body.Name = name;
            var existing = Find(c, name);
            if (existing is null) c.Templates.Add(body); else c.Templates[c.Templates.IndexOf(existing)] = body;
            return null;
        }, _ => Results.Ok(body)))
        .WithSummary("Create or replace a template.");

        tpl.MapDelete("/{name}", (string name, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var existing = Find(c, name);
            if (existing is null) return NotFound($"Template '{name}'");
            c.Templates.Remove(existing);
            return null;
        }, _ => Results.NoContent()));

        api.MapPost("/channels/{channel}/devices/from-template", (string channel, TemplateInstanceBody body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var ch = FindChannel(c, channel);
            if (ch is null) return NotFound($"Channel '{channel}'");
            var t = Find(c, body.Template);
            if (t is null) return NotFound($"Template '{body.Template}'");
            if (t.DriverType != ch.DriverType || !string.Equals(t.PluginId, ch.PluginId, StringComparison.OrdinalIgnoreCase))
                return Json(400, new ErrorBody($"Template '{t.Name}' is for another driver than channel '{channel}'."));
            var missing = t.Parameters.Keys.Where(k => body.Parameters?.ContainsKey(k) != true && string.IsNullOrEmpty(t.Parameters[k])).ToList();
            if (missing.Count > 0) return Json(400, new ErrorBody("Missing parameter value(s): " + string.Join(", ", missing)));
            ch.Devices.Add(TemplateEngine.Instantiate(t, body.Name, body.Parameters));
            return null;
        }, c => Results.Created($"/api/v1/channels/{Uri.EscapeDataString(channel)}/devices/{Uri.EscapeDataString(body.Name)}",
            FindDevice(FindChannel(c, channel)!, body.Name))))
        .WithTags("Templates")
        .WithSummary("Add a device built from a template.");

        api.MapPost("/channels/{channel}/devices/{device}/sync-template", (string channel, string device, bool? removeObsolete, ConfigEditor editor) =>
        {
            TemplateEngine.SyncResult? result = null;
            return editor.EditAsync(c =>
            {
                var ch = FindChannel(c, channel);
                var d = ch is null ? null : FindDevice(ch, device);
                if (d is null) return NotFound($"Device '{channel}.{device}'");
                if (d.Template is null) return Json(400, new ErrorBody("This device was not created from a template."));
                var t = Find(c, d.Template);
                if (t is null) return NotFound($"Template '{d.Template}'");
                result = TemplateEngine.Sync(d, t, removeObsolete == true);
                return null;
            }, _ => Results.Ok(result));
        })
        .WithTags("Templates")
        .WithSummary("Bring a device back in line with its template (tags added/updated; removeObsolete=true also removes tags the template no longer defines).");
    }

    private static DeviceTemplate? Find(GatewayConfig c, string name) =>
        c.Templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
