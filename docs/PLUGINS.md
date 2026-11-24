# Writing a RizzServer driver plugin

A plugin adds a driver (a protocol RizzServer doesn't speak yet) without touching the server. It shows up in the
driver list of a channel, with the settings you describe, and its tags are served over OPC UA like any other.

`samples/RizzServer.Plugin.Simulator` is a complete, working example (about 100 lines).

## 1. Create the project

```powershell
dotnet new classlib -n Acme.Weighbridge -f net10.0
cd Acme.Weighbridge
dotnet add package RizzServer.Sdk --version "1.*"
```

Edit the `.csproj`:

```xml
<PropertyGroup>
  <EnableDynamicLoading>true</EnableDynamicLoading>   <!-- copies the plugin's own dependencies next to it -->
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="RizzServer.Sdk" Version="1.*" ExcludeAssets="runtime" />   <!-- the server supplies the SDK -->
</ItemGroup>
```

(Until the package is published: `dotnet pack RizzServer.Sdk -c Release` in this repository and add the `.nupkg`
folder as a NuGet source, or use a `ProjectReference` like the sample does.)

## 2. Describe the driver and write it

```csharp
using Microsoft.Extensions.Logging;
using RizzServer.Sdk;

public sealed class WeighbridgePlugin : IDriverPlugin
{
    public PluginDescriptor Describe() => new()
    {
        Id = "acme.weighbridge",                 // stored in projects: never change it
        DisplayName = "Acme weighbridge",
        AddressHint = "weight, tare, status",
        MaxDevices = 1,
        Settings =
        [
            new SettingInfo("Host", "Host", "192.168.1.50", SettingLevel.Device),
            new SettingInfo("Port", "Port", "4001", SettingLevel.Device),
            new SettingInfo("Password", "Password", "", SettingLevel.Channel, Secret: true),
        ],
    };

    public PluginDriver Create(DriverContext context) => new WeighbridgeDriver(context);
}

sealed class WeighbridgeDriver(DriverContext context) : PluginDriver(context)
{
    protected override async Task ConnectAsync(CancellationToken ct) { /* open the connection; throw to report why not */ }

    protected override async Task PollAsync(CancellationToken ct)
    {
        foreach (var tag in Context.Tags)
            Publish(tag, await ReadAsync(tag.Address, ct));          // value, quality defaults to Good
    }

    protected override Task DisconnectAsync() => Task.CompletedTask;

    protected override Task WriteAsync(SdkTag tag, object value, CancellationToken ct) => /* optional */ Task.CompletedTask;
}
```

What the server does for you: it calls `ConnectAsync`, then `PollAsync` at the device's scan rate; if either throws,
the device is shown as faulted with your message, all its tags go Bad, `DisconnectAsync` is called and the whole
cycle retries after five seconds. Scaling, units, ranges, the OPC UA nodes, outputs, diagnostics and the audit log all
work with plugin tags with no extra code.

Rules of thumb:

- Publish values with a .NET type that matches the tag (`double` for Double, `int` for Int32 …); the server converts if it can.
- Respect `ct`; the server cancels it when the runtime stops.
- `Context.Settings` has channel and device settings merged; secrets are already decrypted.
- Event-driven protocols can call `Publish` whenever data arrives and use `PollAsync` only as a health check (throw if the link died).
- Don't block threads for long; use `async`.

## 3. Install

Copy the build output (`bin/Release/net10.0/*`) into one folder per plugin:

| Where | Folder |
|---|---|
| next to the program | `plugins/Acme.Weighbridge/` |
| the data folder | `%AppData%\RizzServer\plugins\Acme.Weighbridge\` (Linux/macOS: `~/.config/RizzServer/plugins/…`, or `$RIZZSERVER_DATA_DIR/plugins/…`) |

Restart the server or the app. Check what was found:

```powershell
rizzserver-service plugins
curl -H "Authorization: Bearer $KEY" http://127.0.0.1:5080/api/v1/plugins
```

A plugin that fails to load (wrong SDK version, exception in `Describe`, corrupt file) is listed with the reason; it
never stops the server or other plugins. A project that uses a missing plugin loads, but validation tells you the
plugin is not installed and the channel cannot start.

## 4. Trust

**A plugin is code running inside the server with all its rights.** Only install plugins you trust.

- Every loaded assembly's SHA-256 is logged and listed by `rizzserver-service plugins` and `/api/v1/plugins`.
- To accept only specific builds, create `plugins/trusted.txt` with one SHA-256 per line (`#` starts a comment). Anything
  else is reported as *Rejected* and not loaded.
- `RIZZSERVER_PLUGINS=off` loads no plugins at all.

## Compatibility

The SDK has a contract version (`SdkInfo.ContractVersion`, currently 1). Within a major version of the SDK a plugin keeps
working with newer servers; a plugin built for a newer major version than the server is refused with a clear message.
Each plugin runs in its own assembly load context, so its dependencies (a vendor library, a different JSON version) cannot
clash with the server's or with other plugins'.
