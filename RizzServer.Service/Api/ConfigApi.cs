using System.Text;
using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;
using static RizzServer.Service.Api.ConfigEditor;

namespace RizzServer.Service.Api;

/// <summary>Project configuration: server settings, users, channels, devices and tags.</summary>
public static class ConfigApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var cfg = api.MapGroup("").WithTags("Configuration");

        // ---- whole project ----
        cfg.MapGet("/config", (GatewayRuntime rt) => Results.Ok(new ProjectBody
        {
            Server = ServerBody.From(rt.Config.Server),
            Channels = rt.Config.Channels.Select(Mask).ToList(),
            Templates = rt.Config.Templates.ToList(),
            Northbound = rt.Config.Northbound.Select(Mask).ToList(),
            Redundancy = Mask(rt.Config.Redundancy),
        }))
        .WithSummary("The whole project. Secrets are masked; users are listed by name only.");

        cfg.MapPut("/config", (ProjectBody body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            body.Server.ApplyTo(c.Server);          // users are untouched: manage them via /server/users
            var incoming = body.Channels;
            foreach (var ch in incoming) RestoreSecrets(ch, FindChannel(c, ch.Name));
            c.Channels.Clear();
            foreach (var ch in incoming) c.Channels.Add(ch);
            if (body.Templates is not null)
            {
                c.Templates.Clear();
                foreach (var t in body.Templates) c.Templates.Add(t);
            }
            if (body.Northbound is not null)
            {
                foreach (var n in body.Northbound) RestoreSecrets(n, c.Northbound.FirstOrDefault(o => o.Name == n.Name));
                c.Northbound.Clear();
                foreach (var n in body.Northbound) c.Northbound.Add(n);
            }
            if (body.Redundancy is { } r)
            {
                if (r.PeerApiKey == SecretMask) r.PeerApiKey = c.Redundancy.PeerApiKey;
                c.Redundancy = r;
            }
            return null;
        }, c => Results.Ok(new ProjectBody
        {
            Server = ServerBody.From(c.Server),
            Channels = c.Channels.Select(Mask).ToList(),
            Templates = c.Templates.ToList(),
            Northbound = c.Northbound.Select(Mask).ToList(),
            Redundancy = Mask(c.Redundancy),
        })))
        .WithSummary("Replace server settings and all channels at once. Stop the runtime first.");

        // ---- server settings + users ----
        cfg.MapGet("/server", (GatewayRuntime rt) => Results.Ok(ServerBody.From(rt.Config.Server)));

        cfg.MapPut("/server", (ServerBody body, ConfigEditor editor) => editor.EditAsync(
            c => { body.ApplyTo(c.Server); return null; },
            c => Results.Ok(ServerBody.From(c.Server))));

        cfg.MapPost("/server/users", (UserBody body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            if (string.IsNullOrWhiteSpace(body.Username) || (body.Password?.Length ?? 0) < 8)
                return Json(400, new ErrorBody("A user name and a password of at least 8 characters are required."));
            var hash = PasswordHasher.Hash(body.Password!);
            var existing = c.Server.Users.FirstOrDefault(u => u.Username == body.Username.Trim());
            if (existing is not null) { existing.PasswordHash = hash; if (body.Role is { } role) existing.Role = role; }
            else c.Server.Users.Add(new UserAccount { Username = body.Username.Trim(), PasswordHash = hash, Role = body.Role ?? UserRole.Operator });
            return null;
        }, c => Results.Ok(ServerBody.From(c.Server).Users)))
        .WithSummary("Add a user, or change the password of an existing one.");

        cfg.MapDelete("/server/users/{username}", (string username, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var user = c.Server.Users.FirstOrDefault(u => u.Username == username);
            if (user is null) return NotFound($"User '{username}'");
            c.Server.Users.Remove(user);
            return null;
        }, c => Results.Ok(ServerBody.From(c.Server).Users)));

        // ---- channels ----
        cfg.MapGet("/channels", (GatewayRuntime rt) => Results.Ok(rt.Config.Channels.Select(Mask)));

        cfg.MapGet("/channels/{channel}", (string channel, GatewayRuntime rt) =>
            FindChannel(rt.Config, channel) is { } ch ? Results.Ok(Mask(ch)) : NotFound($"Channel '{channel}'"));

        cfg.MapPost("/channels", (ChannelConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            c.Channels.Add(body);
            return null;
        }, c => Results.Created($"/api/v1/channels/{Uri.EscapeDataString(body.Name)}", Mask(body))))
        .WithSummary("Add a channel (optionally with devices and tags).");

        cfg.MapPut("/channels/{channel}", (string channel, ChannelConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var existing = FindChannel(c, channel);
            if (existing is null) return NotFound($"Channel '{channel}'");
            RestoreSecrets(body, existing);
            c.Channels[c.Channels.IndexOf(existing)] = body;
            return null;
        }, _ => Results.Ok(Mask(body))))
        .WithSummary("Replace a channel, including its devices and tags. The name may change.");

        cfg.MapDelete("/channels/{channel}", (string channel, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var existing = FindChannel(c, channel);
            if (existing is null) return NotFound($"Channel '{channel}'");
            c.Channels.Remove(existing);
            return null;
        }, _ => Results.NoContent()));

        // ---- devices ----
        cfg.MapGet("/channels/{channel}/devices", (string channel, GatewayRuntime rt) =>
            FindChannel(rt.Config, channel) is { } ch ? Results.Ok(Mask(ch).Devices) : NotFound($"Channel '{channel}'"));

        cfg.MapGet("/channels/{channel}/devices/{device}", (string channel, string device, GatewayRuntime rt) =>
            FindChannel(rt.Config, channel) is { } ch && FindDevice(Mask(ch), device) is { } d ? Results.Ok(d) : NotFound($"Device '{channel}.{device}'"));

        cfg.MapPost("/channels/{channel}/devices", (string channel, DeviceConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var ch = FindChannel(c, channel);
            if (ch is null) return NotFound($"Channel '{channel}'");
            ch.Devices.Add(body);
            return null;
        }, c => Results.Created($"/api/v1/channels/{Uri.EscapeDataString(channel)}/devices/{Uri.EscapeDataString(body.Name)}", body)));

        cfg.MapPut("/channels/{channel}/devices/{device}", (string channel, string device, DeviceConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var ch = FindChannel(c, channel);
            var existing = ch is null ? null : FindDevice(ch, device);
            if (ch is null || existing is null) return NotFound($"Device '{channel}.{device}'");
            ch.Devices[ch.Devices.IndexOf(existing)] = body;
            return null;
        }, c => Results.Ok(body)))
        .WithSummary("Replace a device, including its tags. The name may change.");

        cfg.MapDelete("/channels/{channel}/devices/{device}", (string channel, string device, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var ch = FindChannel(c, channel);
            var existing = ch is null ? null : FindDevice(ch, device);
            if (ch is null || existing is null) return NotFound($"Device '{channel}.{device}'");
            ch.Devices.Remove(existing);
            return null;
        }, _ => Results.NoContent()));

        // ---- tags ----
        const string tagsRoute = "/channels/{channel}/devices/{device}/tags";

        cfg.MapGet(tagsRoute, (string channel, string device, GatewayRuntime rt) =>
            FindChannel(rt.Config, channel) is { } ch && FindDevice(ch, device) is { } d ? Results.Ok(d.Tags) : NotFound($"Device '{channel}.{device}'"));

        cfg.MapPut(tagsRoute, (string channel, string device, List<TagConfig> body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var d = DeviceOf(c, channel, device);
            if (d is null) return NotFound($"Device '{channel}.{device}'");
            d.Tags.Clear();
            foreach (var t in body) d.Tags.Add(t);
            return null;
        }, c => Results.Ok(DeviceOf(c, channel, device)!.Tags)))
        .WithSummary("Replace all tags of a device.");

        cfg.MapPost(tagsRoute, (string channel, string device, TagConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var d = DeviceOf(c, channel, device);
            if (d is null) return NotFound($"Device '{channel}.{device}'");
            d.Tags.Add(body);
            return null;
        }, c => Results.Created($"/api/v1/channels/{Uri.EscapeDataString(channel)}/devices/{Uri.EscapeDataString(device)}/tags/{Uri.EscapeDataString(body.Name)}", body)));

        cfg.MapPut(tagsRoute + "/{tag}", (string channel, string device, string tag, TagConfig body, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var d = DeviceOf(c, channel, device);
            var existing = d?.Tags.FirstOrDefault(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));
            if (d is null || existing is null) return NotFound($"Tag '{channel}.{device}.{tag}'");
            d.Tags[d.Tags.IndexOf(existing)] = body;
            return null;
        }, c => Results.Ok(body)));

        cfg.MapDelete(tagsRoute + "/{tag}", (string channel, string device, string tag, ConfigEditor editor) => editor.EditAsync(c =>
        {
            var d = DeviceOf(c, channel, device);
            var existing = d?.Tags.FirstOrDefault(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));
            if (d is null || existing is null) return NotFound($"Tag '{channel}.{device}.{tag}'");
            d.Tags.Remove(existing);
            return null;
        }, _ => Results.NoContent()));

        // ---- CSV (same layout as the desktop app and KEPServerEX) ----
        cfg.MapGet(tagsRoute + ".csv", (string channel, string device, GatewayRuntime rt) =>
            FindChannel(rt.Config, channel) is { } ch && FindDevice(ch, device) is { } d
                ? Results.Text(TagCsv.Format(d.Tags), "text/csv", new UTF8Encoding(true))
                : NotFound($"Device '{channel}.{device}'"))
        .WithSummary("Export a device's tags as CSV.");

        cfg.MapPost(tagsRoute + "/import", async (string channel, string device, HttpRequest request, ConfigEditor editor) =>
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var parsed = TagCsv.Parse(await reader.ReadToEndAsync());
            if (parsed.Tags.Count == 0)
                return Json(400, new ErrorBody("No tags could be read from the CSV.", parsed.Errors));

            int added = 0, updated = 0;
            var result = await editor.EditAsync(c =>
            {
                var d = DeviceOf(c, channel, device);
                if (d is null) return NotFound($"Device '{channel}.{device}'");
                (added, updated) = TagCsv.ApplyTo(d, parsed.Tags);
                return null;
            }, _ => Results.Ok(new { added, updated, skipped = parsed.Errors.Count, errors = parsed.Errors }));
            return result;
        })
        .WithSummary("Add or update tags from a CSV body (text/csv). Existing tags with the same name are updated.");
    }

    private static DeviceConfig? DeviceOf(GatewayConfig c, string channel, string device) =>
        FindChannel(c, channel) is { } ch ? FindDevice(ch, device) : null;
}
