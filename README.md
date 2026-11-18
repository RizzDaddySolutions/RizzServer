# RizzServer

An OPC UA server with a Fluent WPF front end that gathers data from other protocols and serves it as OPC UA nodes. Built on .NET 10.

| Protocol | Driver | Tag address |
|---|---|---|
| Modbus TCP | NModbus | `HR100`, `IR100`, `C5`, `DI5` (zero-based; 32/64-bit types span registers, optional word swap) |
| Modbus RTU (RS-232/485) | NModbus + System.IO.Ports | same as above |
| RS-232 text | System.IO.Ports | field index `0`, `line`, or `re:pattern` |
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
- Security: anonymous access only; unsecured and auto-accept-certificate options are on by default for ease of setup. Turn them off in Server Settings for production.
