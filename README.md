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
- `RizzServer.Sdk` – the small contract for writing your own driver plugin ([docs/PLUGINS.md](docs/PLUGINS.md)); `samples/` has a working simulator plugin
- `RizzServer.App` – Avalonia app. Project tree (Connectivity ▸ channels ▸ devices, Templates, Northbound outputs, OPC UA Server, Quick Client), property-grid editors, tag grid, event log, Start/Stop Runtime in the toolbar

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

Configuration is stored in `%AppData%\RizzServer\config.json` (Windows) or `~/.config/RizzServer/config.json` (Linux/macOS), with passwords encrypted (see Security below). Certificates live in a `pki` folder next to it, and UI preferences (theme, window size and position, pane sizes) in `ui-settings.json`. Set the `RIZZSERVER_DATA_DIR` environment variable to keep all of these in another folder (portable use).

**View ▸ Theme: System / Light / Dark** switches the look; System follows the operating system and is the default. The window remembers its size, position and maximised state. Default endpoint: `opc.tcp://<host>:4840/RizzServer`.

## Protocol Designer (serial protocols)

**Tools ▸ Protocol Designer…** reverse-engineers an undocumented serial protocol and turns it into a ready-to-run channel. The **Binary frames** tab targets the RS-232 binary frames driver; the **Text & commands** tab (below) targets the RS-232 text and command/response drivers. The capture bar is shared.

1. **Capture** bytes from a serial port, open a capture file (raw bytes or a hex dump), or paste hex. Captures can be saved as hex text.
2. **Framing**: *Detect* works out the header, the fixed length or the length field, and the checksum (Sum8, Xor8, CRC16-Modbus) from the data, and tells you how sure it is. Everything stays editable; *IdleGap* mode splits frames at line silences for protocols with no header.
3. **Frames** appear as a hex table with each byte coloured by role (header, length, checksum, your fields). Click a byte and the **inspector** shows every way to read it (8/16/32-bit, either byte order, signed, float) with last/min/max and a smoothness score. A real measurement moves steadily between frames, a misread one doesn't. Implausible float readings are scored 0.
4. **Fields**: add readings as fields, or let **Suggest** propose them (counters are recognised and skipped). Give them names and scales.
5. **Create channel in project** builds the channel, device and tags (`offset:type[:scale]` addresses) with the detected framing and your port settings. The driver cuts and decodes frames with the same code the designer used, so what you saw is what you get.

### Text & commands tab

For line-based protocols. A **terminal** sends a command (escapes `\r \n \xHH`, choose the command terminator) once or on a poll timer, and shows everything sent and received. Replies are grouped per command; lines that arrive on their own are grouped as the device's stream. The **line ending** and **separator** are detected from the data (and stay editable).

Click a reply to see it split into values with their numeric reading. **Add as field** picks a whole column (index) or, when the number is mixed with text (`58%`, `T=21.5`), a pattern; **Whole reply** is for commands that answer with just the value; **Suggest** offers a field for every value that changes. Each field shows its live value from the latest reply.

**Create channel** builds an RS-232 command/response channel (`command|parse` addresses) when fields have commands, or an RS-232 text channel (bare parse addresses) for a device that talks on its own. A channel can't mix the two, and patterns in command addresses can't contain `|`. The drivers extract values with the same code the designer shows.

Detection is heuristic: it needs a few dozen frames, and when a protocol has no checksum the exact frame start can be ambiguous, so check the header column colours. Live capture needs a local serial port, so it is disabled while connected to a remote service (files and pasted hex still work). Capture from a real device has not been tried yet; the tests use synthetic protocols and a fake serial source that answers commands.

## Headless service and REST API

`RizzServer.Service` runs the same runtime (drivers + OPC UA server) with no UI: as a console process, a Windows service, a systemd service or in Docker. It loads the project file, starts it, and exposes a REST API for configuration and monitoring.

```powershell
dotnet run --project RizzServer.Service                      # API on http://127.0.0.1:5080; the first run prints an admin API key once
dotnet run --project RizzServer.Service -- keys create grafana Viewer   # more keys, with a role
```

```bash
KEY=...   # the key printed on first run
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
| Config as code | `GET /config/export?format=yaml`, `GET /config/schema`, `POST /config/validate`, `/config/diff`, `/config/apply[?restart=true]` (body = project file, JSON or YAML) |
| Data model | `GET/PUT/DELETE /templates[/{name}]`, `POST /channels/{c}/devices/from-template`, `POST .../devices/{d}/sync-template` |
| Outputs | `GET/PUT/DELETE /outputs[/{name}]`, `GET /outputs/status`, `GET /outputs/kinds` |
| Commissioning | `GET /diagnostics`, `GET /diagnostics/{c}/{d}/events`, `POST /commissioning/test`, `/commissioning/read`, `/commissioning/modbus-scan`, `GET /commissioning/ports` |
| Security | `GET /security/report`, `/security/keys` (create, revoke), `GET /audit`, `/audit/verify` |
| Redundancy | `GET /redundancy`, `POST /redundancy/demote`, `/promote` |
| System | `GET /drivers` (every driver and its settings), `GET /plugins`, `GET /log`, `GET /health` (no key), `GET /openapi/v1.json` |

- **Same rules as the desktop app.** Names must be unique and period-free, settings are validated against the driver catalogue, and a change is applied to a copy, validated as a whole and then saved, so a bad request never leaves a half-edited project. Configuration changes return `409` while the runtime runs; stop it, change, start it again.
- **Authentication.** Every call except `/health` and the OpenAPI document needs `Authorization: Bearer <key>` (or `X-Api-Key`). Keys have a role (Viewer reads, Operator also writes tag values and runs commissioning tests, Admin does everything), are stored only as hashes, and are created with `rizzserver-service keys create <name> <role>` or `POST /security/keys`. The first run creates one admin key and prints it once. `RIZZSERVER_API_KEY` adds an admin key from the environment. Keys from the earlier single-key version are migrated. Passwords of drivers (MQTT, OPC UA client) are never returned (`********`); sending the mask back keeps the stored value.
- **Network exposure.** The API listens on loopback only by default. To expose it, use an `https://` URL in `Urls` (a self-signed certificate is created, or set `CertificatePath` and `CertificatePassword`); the service refuses to start on a non-loopback address with no key, or over plain HTTP unless `AllowInsecureRemote` is set (for a TLS-terminating reverse proxy).
- **Data folder.** `service.json`, `config.json` and `pki/` live in `RIZZSERVER_DATA_DIR` (default: the per-user application-data folder). The desktop app uses the same folder, so on one machine you can configure with the UI and run with the service; do not run both at once (they would share the OPC UA port).
- **Settings** (`service.json` / environment): `Urls` / `RIZZSERVER_API_URLS`, `ApiKeys` / `RIZZSERVER_API_KEY`, `CertificatePath`, `AllowInsecureRemote`, `MaxFailedAuth`, `AutoStart` / `RIZZSERVER_AUTOSTART` (start the runtime with the process; default true).

### Manage a service from the desktop app

**File ▸ Connect to Service…** (address + API key) opens the project of a headless service in the normal UI. You edit a copy exactly as for a local project; **Save (Ctrl+S)** pushes it to the service, which validates it and answers with the problems if it refuses. **Start/Stop Runtime** control the service's runtime, and the tree's status dots, the Quick Client (values stream in live, writes go through the service to the device) and the event log show the service's. The status bar shows `Remote: <address>` and `● unsaved changes` when your copy differs from the service; Reload, Disconnect and closing ask before discarding. **Disconnect** returns to your local project; the service keeps running, and closing the app never stops it. If the service becomes unreachable the status bar says so and the app reconnects by itself.

- Editing is locked while the service's runtime runs, like locally; a start or stop done by someone else shows up within a second or two.
- Users are managed on the service directly (the password is hashed there); serial ports can't be listed for the remote machine, so the wizard asks you to type the port name.
- The API key is never saved, only the last address. Use HTTPS (reverse proxy) when connecting across a network.

Deployment files: `Dockerfile` and `deploy/docker-compose.yml`, `deploy/rizzserver.service` (systemd), `deploy/install-windows-service.ps1`. Build a self-contained service with `./installer/publish-portable.ps1 -Component Service`. The Docker, systemd and Windows-service files have not been run yet; the service itself is tested as a plain process (Windows).
## Config as code

A project is plain data, so it can live in Git and be reviewed like code.

```powershell
rizzserver-service export project.yaml          # JSON or YAML by extension; secrets become ${env:NAME} references
rizzserver-service validate project.yaml        # exit 1 with the problems listed (use it in CI)
rizzserver-service diff project.yaml            # what applying it would change, by name (against the stored project)
rizzserver-service diff before.yaml after.yaml
rizzserver-service apply project.yaml [--dry-run]
rizzserver-service schema > project.schema.json # JSON Schema for editor completion
rizzserver-service --config project.yaml        # run straight from the file; edits to the file reload the running project
```

- Settings are written in sorted order so diffs are small. YAML may write numbers and booleans unquoted (`Port: 502`); they are read as the text they are.
- **Secrets stay out of the file.** An exported file has `Password: ${env:RIZZ_BROKER_PASSWORD}`; the CLI lists the variables to set. `${file:/run/secrets/x}` reads a secret from a file. References are resolved when the runtime starts and never written back. `diff` shows a changed secret as `secret:ab12cd34`, never its value.
- With `--config`, the file is the truth: the service validates it at startup, refuses to start if it is invalid, reloads when it changes (a bad edit is logged and ignored, the running project keeps running) and its REST API refuses configuration edits.
- `POST /api/v1/config/apply?restart=true` does the same through the API, e.g. from a CI job.

## Data model

- **Tag metadata**: description, units, linear scaling (`engineering = raw × Scale + Offset`; use a Float/Double tag), and an engineering range. A value outside the range is served with Uncertain quality and writes outside it are refused. OPC UA clients see the description and the standard `EngineeringUnits` and `EURange` properties. Writes are converted back to the raw value.
- **Device templates**: define a device once (driver, settings, scan rate, tags) with `{parameters}` in names, addresses and settings, also `{base+10}` arithmetic. *Add Device from Template* (right-click a channel) or `POST /channels/{c}/devices/from-template` stamps out devices; *Save as Template* (right-click a device) starts from an existing one; `sync-template` brings a device back in line after the template changed (new tags are added, changed ones updated, hand-made tags kept; `removeObsolete=true` also removes tags the template dropped).

## Northbound outputs

Outputs publish tag values to other systems while the runtime runs (tree: **Northbound**):

- **MQTT publisher**: one topic per tag (`rizzserver/{channel}/{device}/{tag}`, `{path}` also available), JSON (`path`, `value`, `quality`, `timestamp`, `units`) or the bare value, retained, QoS 0-2, on change or at an interval, an online/offline status topic with a last will, a fresh snapshot after every reconnect.
- **HTTP webhook**: batches of values as JSON to a URL on an interval, optional `Authorization` header, retry with the latest value per tag after a failure.
- A path filter per output (`Plant.PLC1.*;*.Alarm*`), live counters and the last error in the UI and `GET /outputs/status`. A broker that is down never affects the runtime.

## Commissioning

- **Per-device diagnostics** (device page, `GET /diagnostics`): state and uptime, reconnects, polls and failures, last and average poll time, update rate, good and bad tags, and the last events with the reason for every fault.
- **Test connection** (device page, `POST /commissioning/test`): connects to the device on its own with its settings and reads every tag once, saying per tag what came back or why not. It works with the runtime stopped.
- **Trial reads** (`/commissioning/read`) try addresses that are not tags yet; **Modbus scan** (`/commissioning/modbus-scan`) finds which addresses of a table answer and returns the readable ranges with their values.
- The **Protocol Designer** (Tools menu) works out undocumented serial protocols (see above).

## Security

- **Secrets at rest**: driver, output and peer passwords are encrypted in `config.json` (AES-256-GCM, key in `secret.key` next to it or in `RIZZSERVER_SECRET_KEY`); plain values from older files are encrypted on the next save. Back up `secret.key` with the project: without it the passwords cannot be read.
- **OPC UA**: users have roles. Anonymous sessions and Viewers read; Operators and Admins write. Five wrong passwords lock a user name for five minutes. Passwords are PBKDF2 hashes.
- **REST API**: hashed keys with roles, blocking of an address after repeated wrong keys, HTTPS (see above).
- **Audit log** (`audit.log`): logins and failures, tag writes (who, which tag, which value), API changes, key and user changes, failovers. It is a hash chain: `rizzserver-service audit verify` (or `GET /audit/verify`) proves nothing was edited or removed in the middle. `audit tail` prints the latest entries. It does not stop someone with file access from deleting the whole file; ship it elsewhere if that matters.
- **Security review**: Server page ▸ *Check now*, `GET /security/report` or `rizzserver-service security-report` lists what is exposed (anonymous access, unencrypted OPC UA, auto-accepted certificates, a key-less or plain-HTTP API, brokers without TLS) with the fix. `rizzserver-service harden` applies the OPC UA fixes (creating an admin user with a generated password it prints once).
- Defaults are unchanged so a new project works out of the box; the review exists to make closing them a one-step job.

## Redundancy

Two service instances form a pair. Configure each (in the project file; `Redundancy`):

```yaml
Redundancy:
  Enabled: true
  Role: Primary            # the other node: Secondary
  PeerUrl: https://plc2:5080
  PeerApiKey: ${env:RIZZ_PEER_API_KEY}   # an Admin key of the peer
  PeerCertificateThumbprint: ""          # SHA-256 of a self-signed peer certificate (pins it)
  HeartbeatMs: 1000
  FailoverAfterMs: 5000
  SyncConfig: true
  FailBack: true
```

- The **Primary** runs the drivers. The **Secondary** is a warm standby: its OPC UA server is up with ServiceLevel 100 and its drivers are idle (no second connection to your devices). When the Primary has not answered for `FailoverAfterMs`, the Secondary connects its drivers, starts its outputs and raises its ServiceLevel to 250; OPC UA clients that use server redundancy (or a failover list) move over. Failover took about 2-3 s in tests with a 1.5 s limit.
- While the Primary is healthy the Secondary **copies its project** (users, channels, templates, outputs; not its own OPC UA port or name) whenever it changes.
- With `FailBack`, the Primary asks the Secondary to hand control back when it returns. `POST /redundancy/demote` on the active node is a **planned switchover**: it steps down and stays out for 30 s so the other node takes over.
- Limits: this is failover, not state replication: values are re-read after a takeover. Drivers that cannot share a device (a serial port) work because only the active node connects. If the two nodes cannot see each other but both can reach the devices, both become active until they see each other again (then the Primary wins); give them a reliable link, ideally a dedicated one. Use HTTPS between the nodes: the project (with secrets) travels over it.

## Driver plugins

Write a driver for a protocol RizzServer does not speak, in a few dozen lines, without touching the server: [docs/PLUGINS.md](docs/PLUGINS.md). A plugin is a class library built against `RizzServer.Sdk`; copy its output to `plugins/<name>/` (next to the program or in the data folder) and it appears in the driver list with its own settings. Plugins run in their own load context; failures are reported per plugin; every assembly's SHA-256 is logged and `plugins/trusted.txt` can restrict loading to builds you approve. **A plugin is code running with the server's rights: install only plugins you trust.** `samples/RizzServer.Plugin.Simulator` is a driver that needs no hardware (sine, ramp, random, counter, writable memory cells), handy for trying everything above.

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

## Large projects

**Publish only on change** (OPC UA Server settings, `PublishOnChange` in the project file and `/server`) is on by default: a poll whose value and quality did not change is not published to OPC UA, outputs or the event stream, and a tag's timestamp means "last change". In a test with 1,000,000 tags scanned once a second (10% changing), this cut the idle server load from 2.3 to 0.9 cores and let a subscribed client keep up (78k instead of 21k notifications/s). Turn it off if a consumer needs every poll as a heartbeat. List endpoints (`/channels`, `.../devices`, `.../tags`) take `skip`/`take` and return the total in `X-Total-Count`; `summary=true` returns counts instead of tags.

## Notes

- **Edit ▸ New Channel…** opens the wizard: channel name → driver → communication settings (detected serial ports are listed) → first device (name, device settings, scan rate) → summary. **New Device…** runs the device half of it for an existing channel. Tags are added in the device's tag grid.
- **Import CSV… / Export CSV…** (File menu or the tag grid) bring tags in and out in KEPServerEX's column layout: `Tag Name, Address, Data Type, Client Access`. Kepware type names (Short, Word, Long, DWord, LLong, QWord, Float, Double, Boolean, String) and `Read Only` / `Read/Write` are understood, comma, semicolon or tab delimiters are detected, and extra columns are ignored. Existing tags with the same name are updated; bad rows are skipped and listed. Addresses are driver-specific, so Kepware addresses will need editing for the RizzServer driver.
- **Right-click** works everywhere: Connectivity (New Channel…), a channel (New Device…, Disable/Enable, Duplicate, Delete), a device (New Tag, Import/Export CSV, Disable/Enable, Duplicate, Delete), OPC UA Server (Copy Endpoint URL, Start/Stop), Project (Save, Start/Stop, Expand/Collapse All), and every grid (channels, devices, tags, users, live values, event log). Menus act on the item under the pointer; editing items are greyed out while the runtime is running.
- **Rename** a channel or device with **F2**, Edit ▸ Rename…, or right-click ▸ Rename…. Names must be unique among their siblings and cannot contain a period; renaming changes the OPC UA NodeIds (`Channel.Device.Tag`), so it is locked while the runtime runs.
- Add channels and tags in the project tree, then press **Start Runtime**. Configuration is locked while the runtime is running.
- OPC DA is Windows-only and needs the OPC Core Components. The Automation wrapper is normally 32-bit, so build the app for x86 to use it. On Linux/macOS that driver reports "not supported"; everything else works.
- Serial drivers use the OS port names (`COM3`, `/dev/ttyUSB0`, `/dev/tty.usbserial-…`). On Linux the user needs access to the device (usually the `dialout` group).
- Security: optional user name/password login (salted PBKDF2 hashes; manage users under OPC UA Server) and anonymous access can be switched off. Unsecured connections and auto-accept-certificates are on by default for easy setup; turn them off for production, otherwise passwords travel unencrypted.
