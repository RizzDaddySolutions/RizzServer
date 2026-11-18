# RizzServer

An OPC UA server with a Fluent WPF front end that gathers data from other protocols and serves it as OPC UA nodes. Built on .NET 10.

| Protocol | Driver | Tag address |
|---|---|---|
| Modbus TCP | NModbus | `HR100`, `IR100`, `C5`, `DI5` (zero-based; 32/64-bit types span registers, optional word swap) |
| Modbus RTU (RS-232/485) | NModbus + System.IO.Ports | same as above |
| RS-232 text | System.IO.Ports | field index `0`, `line`, or `re:pattern` |
| RS-232 command/response | System.IO.Ports | `command\|parse\|writeTemplate` e.g. `READ?\|0`, `W\|re:([-\d.]+)`, `SP?\|\|SP {0}` |
| RS-232 binary frames | System.IO.Ports | `offset:type[:scale]` e.g. `4:i16:0.1`, `6:f32le`, `3:bit2` (header sync, fixed/length-field frames, Sum8/Xor8/CRC16 check; writes via a template: ``4:i16:0.1\|AA55 05 {v} {ck}``) |
| MQTT | MQTTnet | `topic` or `topic\|json.path` (wildcards `+`/`#` allowed for reads) |
| OPC DA (classic) | OPC Automation (late-bound COM) | OPC item ID |

## Layout

- `RizzServer.Core` – config models, `GatewayEngine` (drivers → live tag table), driver catalog
- `RizzServer.Drivers` – protocol drivers; each derives from `DriverBase` (connect/poll/auto-reconnect)
- `RizzServer.Server` – OPC UA endpoint (OPC Foundation stack); address space is `Objects/RizzServer/<Connection>/<Tag>`
- `RizzServer.App` – WPF-UI (Fluent) app: dashboard, connections/tags editor, live data with writes, log, server settings

Writable tags push OPC UA writes back to the device through the owning driver.

## Run

```powershell
dotnet run --project RizzServer.App
```

Configuration is stored in `%AppData%\RizzServer\config.json` (including any MQTT password, in plain text). Certificates live in `%AppData%\RizzServer\pki`. Default endpoint: `opc.tcp://<host>:4840/RizzServer`.

## Notes

- Add connections and tags, press **Start**. Configuration is locked while the server runs.
- OPC DA needs the OPC Core Components. The Automation wrapper is normally 32-bit, so build the app for x86 to use it.
- Security: optional user name/password login (salted PBKDF2 hashes; manage users in Server Settings) and anonymous access can be switched off. Unsecured connections and auto-accept-certificates are on by default for easy setup; turn them off for production, otherwise passwords travel unencrypted.
