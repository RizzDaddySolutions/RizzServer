using RizzServer.Core;
using RizzServer.Core.Api;
using RizzServer.Server;

namespace RizzServer.Service.Api;

public static class RedundancyApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/redundancy").WithTags("Redundancy");

        g.MapGet("", (RedundancyManager m) => Results.Ok(m.Status()))
         .WithSummary("This node's role and state (Active / Standby), ServiceLevel and what it sees of its peer.");

        g.MapGet("/project", (GatewayRuntime rt, HttpContext http) =>
        {
            // The peer needs the secrets to run the same drivers: Admin only, and meant for HTTPS.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(ConfigStore.Serialize(rt.Config), "application/json");
        })
         .WithSummary("The whole project including secrets, for the peer to copy. Admin key; use HTTPS between the nodes.");

        g.MapPost("/demote", async (RedundancyManager m, bool? handback) =>
        {
            // handback=true is the primary asking for control back after a failover: no hold, the primary takes over at once.
            await m.DemoteAsync(handback == true ? "control handed back to the primary" : "stepped down by an operator", manual: handback != true);
            return Results.Ok(m.Status());
        })
         .WithSummary("Step down: this node becomes the standby and holds off taking control back for a while (planned maintenance).");

        g.MapPost("/promote", async (RedundancyManager m) =>
        {
            await m.ManualPromoteAsync();
            return Results.Ok(m.Status());
        })
         .WithSummary("Take control now (this node must be a running standby).");
    }
}
