# RizzServer

A cross-platform (Windows, Linux, macOS) OPC UA server with an Avalonia desktop UI inspired by industrial configuration tools. It gathers data from other protocols and serves it as OPC UA nodes. Built on .NET 10.

| Protocol | Driver | Tag address |
|---|---|---|
| Modbus TCP | NModbus | `HR100`, `IR100`, `C5`, `DI5` (zero-based; 32/64-bit types span registers, optional word swap) |
| Modbus RTU (RS-232/485) | NModbus + System.IO.Ports | same as above |
| RS-232 text | System.IO.Ports | field index `0`, `line`, or `re:pattern` |
| RS-232 command/response | System.IO.Ports | `command\|parse\|writeTemplate` e.g. `READ?\|0`, `W\|re:([-\d.]+)`, `SP?\|\|SP {0}` |
| RS-232 binary frames | System.IO.Ports | `offset:type[:scale]` e.g. `4:i16:0.1`, `6:f32le`, `3:bit2` (header sync, fixed/length-field frames, Sum8/Xor8/CRC16 check; writes via a template: ``4:i16:0.1\|AA55 05 {v} {ck}``) |
| MQTT | MQTTnet | `topic` or `topic\|json.path` (wildcards `+`/`#` allowed for reads) |
| OPC DA (classic, Windows only) | OPC Automation (late-bound COM) | OPC item ID |
| OPC UA client | OPC Foundation stack | NodeId string, e.g. `ns=2;s=Pressure`, `i=2258` (subscription; anonymous or user/password; read/write) |

## Layout

- `RizzServer.Core` – config models, `GatewayEngine` (drivers → live tag table), driver catalog
- `RizzServer.Drivers` – protocol drivers; each derives from `DriverBase` (connect/poll/auto-reconnect)
- `RizzServer.Server` – OPC UA endpoint (OPC Foundation stack); address space is `Objects/RizzServer/<Channel>/<Tag>`
- `RizzServer.App` – Avalonia app. Project tree (Connectivity ▸ channels, OPC UA Server, Quick Client), property-grid editors, tag grid, event log, Start/Stop Runtime in the toolbar

Writable tags push OPC UA writes back to the device through the owning driver.

## Run

```powershell
dotnet run --project RizzServer.App
```

Configuration is stored in `%AppData%\RizzServer\config.json` (Windows) or `~/.config/RizzServer/config.json` (Linux/macOS), including any MQTT password in plain text. Certificates live in a `pki` folder next to it. Default endpoint: `opc.tcp://<host>:4840/RizzServer`.

## Windows installer

```powershell
./installer/build.ps1                 # version from Directory.Build.props
./installer/build.ps1 -Version 1.2.0
```

Produces `artifacts/installer/RizzServer-<version>-x64.msi` (WiX 5, self-contained .NET 10 – no runtime needed on the target PC). It installs per machine to `Program Files\RizzServer`, adds a Start menu shortcut and upgrades in place when you install a newer version. Uninstall keeps your settings in `%AppData%\RizzServer`. Windows Firewall may ask to allow the OPC UA port the first time the server starts.

## Portable builds (Linux, macOS, Windows)

```powershell
./installer/publish-portable.ps1                       # win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64
./installer/publish-portable.ps1 -Runtimes linux-x64
```

Writes self-contained archives to `artifacts/portable/` (`.zip` for Windows, `.tar.gz` otherwise). Extract and run `RizzServer`. Linux/macOS builds are cross-published from Windows and have not been run on those systems yet.

## Notes

- Add channels and tags in the project tree, then press **Start Runtime**. Configuration is locked while the runtime is running.
- OPC DA is Windows-only and needs the OPC Core Components. The Automation wrapper is normally 32-bit, so build the app for x86 to use it. On Linux/macOS that driver reports "not supported"; everything else works.
- Serial drivers use the OS port names (`COM3`, `/dev/ttyUSB0`, `/dev/tty.usbserial-…`). On Linux the user needs access to the device (usually the `dialout` group).
- Security: optional user name/password login (salted PBKDF2 hashes; manage users under OPC UA Server) and anonymous access can be switched off. Unsecured connections and auto-accept-certificates are on by default for easy setup; turn them off for production, otherwise passwords travel unencrypted.
