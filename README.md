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
- `RizzServer.Server` – OPC UA endpoint (OPC Foundation stack); address space is `Objects/RizzServer/<Channel>/<Device>/<Tag>` (NodeIds `ns=2;s=Channel.Device.Tag`)
- `RizzServer.App` – Avalonia app. Project tree (Connectivity ▸ channels ▸ devices, OPC UA Server, Quick Client), property-grid editors, tag grid, event log, Start/Stop Runtime in the toolbar

## Channels, devices and tags

As in Kepware: a **channel** is the communication link (driver plus shared settings such as the COM port, broker or endpoint), a **device** is one endpoint on it (Modbus unit ID, scan rate, protocol framing) and holds the **tags**.

- Modbus RTU and RS-232 command/response channels are multi-drop: several devices share the serial port, with exchanges serialised on the bus.
- RS-232 text and binary-frame drivers read a continuous stream, so those channels hold exactly one device.
- MQTT, OPC UA client and OPC DA open one connection per device (MQTT client IDs get the device name appended).
- Configs saved by the earlier single-level version are migrated automatically: each old connection becomes a channel with one device named `Device1`, so tag NodeIds gain a `Device1` level.

Writable tags push OPC UA writes back to the device through the owning driver.

## Run

```powershell
dotnet run --project RizzServer.App
```

Configuration is stored in `%AppData%\RizzServer\config.json` (Windows) or `~/.config/RizzServer/config.json` (Linux/macOS), including any MQTT password in plain text. Certificates live in a `pki` folder next to it, and UI preferences (theme, window size and position, pane sizes) in `ui-settings.json`. Set the `RIZZSERVER_DATA_DIR` environment variable to keep all of these in another folder (portable use).

**View ▸ Theme: System / Light / Dark** switches the look; System follows the operating system and is the default. The window remembers its size, position and maximised state. Default endpoint: `opc.tcp://<host>:4840/RizzServer`.

## Protocol Designer (binary serial protocols)

**Tools ▸ Protocol Designer…** reverse-engineers an undocumented serial protocol and turns it into a ready-to-run channel for the RS-232 binary frames driver.

1. **Capture** bytes from a serial port, open a capture file (raw bytes or a hex dump), or paste hex. Captures can be saved as hex text.
2. **Framing**: *Detect* works out the header, the fixed length or the length field, and the checksum (Sum8, Xor8, CRC16-Modbus) from the data, and tells you how sure it is. Everything stays editable; *IdleGap* mode splits frames at line silences for protocols with no header.
3. **Frames** appear as a hex table with each byte coloured by role (header, length, checksum, your fields). Click a byte and the **inspector** shows every way to read it (8/16/32-bit, either byte order, signed, float) with last/min/max and a smoothness score. A real measurement moves steadily between frames, a misread one doesn't. Implausible float readings are scored 0.
4. **Fields**: add readings as fields, or let **Suggest** propose them (counters are recognised and skipped). Give them names and scales.
5. **Create channel in project** builds the channel, device and tags (`offset:type[:scale]` addresses) with the detected framing and your port settings. The driver cuts and decodes frames with the same code the designer used, so what you saw is what you get.

Detection is heuristic: it needs a few dozen frames, and when a protocol has no checksum the exact frame start can be ambiguous, so check the header column colours. Live capture needs a local serial port, so it is disabled while connected to a remote service (files and pasted hex still work). Capture from a real device has not been tried yet; the tests use synthetic protocols and a fake serial source.

## Headless service and REST API

`RizzServer.Service` runs the same runtime (drivers + OPC UA server) with no UI: as a console process, a Windows service, a systemd service or in Docker. It loads the project file, starts it, and exposes a REST API for configuration and monitoring.

```powershell
dotnet run --project RizzServer.Service                      # API on http://127.0.0.1:5080
dotnet run --project RizzServer.Service -- --show-api-key    # print the API key (generated on first run)
```

```bash
KEY=$(rizzserver-service --show-api-key)
curl -H "Authorization: Bearer $KEY" http://127.0.0.1:5080/api/v1/runtime
curl -H "Authorization: Bearer $KEY" http://127.0.0.1:5080/api/v1/tags
curl -X PUT -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" -d '{"value": 42}' http://127.0.0.1:5080/api/v1/tags/Plant.PLC.Setpoint
curl -N -H "Authorization: Bearer $KEY" "http://127.0.0.1:5080/api/v1/stream?snapshot=true"      # live changes (Server-Sent Events)
```

| Area | Endpoints (`/api/v1`) |
|---|---|
| Runtime | `GET /runtime`, `POST /runtime/start`, `/stop`, `/restart`, `GET /devices/status` |
| Live data | `GET /tags[?channel=&device=&quality=]`, `GET /tags/{Channel.Device.Tag}`, `PUT /tags/{path}` (write), `GET /stream` (SSE) |
| Configuration | `GET/PUT /config`, `/server`, `/server/users`, `/channels[/{c}]`, `/channels/{c}/devices[/{d}]`, `.../tags[/{t}]`, `.../tags.csv`, `POST .../tags/import` |
| System | `GET /drivers` (every driver and its settings), `GET /log`, `GET /health` (no key), `GET /openapi/v1.json` |

- **Same rules as the desktop app.** Names must be unique and period-free, settings are validated against the driver catalogue, and a change is applied to a copy, validated as a whole and then saved, so a bad request never leaves a half-edited project. Configuration changes return `409` while the runtime runs; stop it, change, start it again.
- **Authentication.** Every call except `/health` and the OpenAPI document needs `Authorization: Bearer <key>` (or `X-Api-Key`). The key is generated on first run into `service.json` (`--new-api-key` rotates it; `RIZZSERVER_API_KEY` overrides it). Passwords of drivers (MQTT, OPC UA client) are never returned (`********`); sending the mask back keeps the stored value.
- **Network exposure.** The API listens on loopback only by default. To expose it, change `Urls` in `service.json` (or `RIZZSERVER_API_URLS`) and put TLS in front (reverse proxy) or configure Kestrel HTTPS; the service warns when a non-loopback address uses plain HTTP and refuses to start with no key on a non-loopback address.
- **Data folder.** `service.json`, `config.json` and `pki/` live in `RIZZSERVER_DATA_DIR` (default: the per-user application-data folder). The desktop app uses the same folder, so on one machine you can configure with the UI and run with the service; do not run both at once (they would share the OPC UA port).
- **Settings** (`service.json` / environment): `Urls` / `RIZZSERVER_API_URLS`, `ApiKey` / `RIZZSERVER_API_KEY`, `AutoStart` / `RIZZSERVER_AUTOSTART` (start the runtime with the process; default true).

### Manage a service from the desktop app

**File ▸ Connect to Service…** (address + API key) opens the project of a headless service in the normal UI. You edit a copy exactly as for a local project; **Save (Ctrl+S)** pushes it to the service, which validates it and answers with the problems if it refuses. **Start/Stop Runtime** control the service's runtime, and the tree's status dots, the Quick Client (values stream in live, writes go through the service to the device) and the event log show the service's. The status bar shows `Remote: <address>` and `● unsaved changes` when your copy differs from the service; Reload, Disconnect and closing ask before discarding. **Disconnect** returns to your local project; the service keeps running, and closing the app never stops it. If the service becomes unreachable the status bar says so and the app reconnects by itself.

- Editing is locked while the service's runtime runs, like locally; a start or stop done by someone else shows up within a second or two.
- Users are managed on the service directly (the password is hashed there); serial ports can't be listed for the remote machine, so the wizard asks you to type the port name.
- The API key is never saved, only the last address. Use HTTPS (reverse proxy) when connecting across a network.

Deployment files: `Dockerfile` and `deploy/docker-compose.yml`, `deploy/rizzserver.service` (systemd), `deploy/install-windows-service.ps1`. Build a self-contained service with `./installer/publish-portable.ps1 -Component Service`. The Docker, systemd and Windows-service files have not been run yet; the service itself is tested as a plain process (Windows).
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

- **Edit ▸ New Channel…** opens the wizard: channel name → driver → communication settings (detected serial ports are listed) → first device (name, device settings, scan rate) → summary. **New Device…** runs the device half of it for an existing channel. Tags are added in the device's tag grid.
- **Import CSV… / Export CSV…** (File menu or the tag grid) bring tags in and out in KEPServerEX's column layout: `Tag Name, Address, Data Type, Client Access`. Kepware type names (Short, Word, Long, DWord, LLong, QWord, Float, Double, Boolean, String) and `Read Only` / `Read/Write` are understood, comma, semicolon or tab delimiters are detected, and extra columns are ignored. Existing tags with the same name are updated; bad rows are skipped and listed. Addresses are driver-specific, so Kepware addresses will need editing for the RizzServer driver.
- **Right-click** works everywhere: Connectivity (New Channel…), a channel (New Device…, Disable/Enable, Duplicate, Delete), a device (New Tag, Import/Export CSV, Disable/Enable, Duplicate, Delete), OPC UA Server (Copy Endpoint URL, Start/Stop), Project (Save, Start/Stop, Expand/Collapse All), and every grid (channels, devices, tags, users, live values, event log). Menus act on the item under the pointer; editing items are greyed out while the runtime is running.
- **Rename** a channel or device with **F2**, Edit ▸ Rename…, or right-click ▸ Rename…. Names must be unique among their siblings and cannot contain a period; renaming changes the OPC UA NodeIds (`Channel.Device.Tag`), so it is locked while the runtime runs.
- Add channels and tags in the project tree, then press **Start Runtime**. Configuration is locked while the runtime is running.
- OPC DA is Windows-only and needs the OPC Core Components. The Automation wrapper is normally 32-bit, so build the app for x86 to use it. On Linux/macOS that driver reports "not supported"; everything else works.
- Serial drivers use the OS port names (`COM3`, `/dev/ttyUSB0`, `/dev/tty.usbserial-…`). On Linux the user needs access to the device (usually the `dialout` group).
- Security: optional user name/password login (salted PBKDF2 hashes; manage users under OPC UA Server) and anonymous access can be switched off. Unsecured connections and auto-accept-certificates are on by default for easy setup; turn them off for production, otherwise passwords travel unencrypted.
