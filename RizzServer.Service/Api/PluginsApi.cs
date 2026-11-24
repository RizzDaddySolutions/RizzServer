using RizzServer.Core;
using RizzServer.Drivers;

namespace RizzServer.Service.Api;

public static class PluginsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/plugins", () => Results.Ok(new
        {
            folders = PluginLoader.SearchFolders,
            note = "Plugins run inside the server with its full rights: install only plugins you trust. Add the SHA-256 values you accept to plugins/trusted.txt to refuse everything else.",
            plugins = PluginLoader.Status,
        }))
        .WithTags("System")
        .WithSummary("Driver plugins found at startup: loaded, rejected or failed (with the reason), and their SHA-256.");
    }
}
