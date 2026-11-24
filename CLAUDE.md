# RizzServer – notes for Claude

## RizzDaddySolutions application standards

These apply to every RizzDaddySolutions project; the user should not have to restate them.

- Cross-platform (Windows, Linux, macOS), **.NET 10**, **Avalonia** UI. Do not ask about these.
- The main window has a **menu strip**.
- A **View** menu always offers **Theme: System**, **Theme: Light**, **Theme: Dark** (System is the default and follows the OS).
- A **Help** menu has **About**, which opens an About window **styled like RizzExplorer's**.
- **Accents use RizzExplorer's blue**: accent `#0078D4`, selection `#115990` (hover `#1A6BAE`, pressed `#0E4B7B`), bullets `#60CDFF`.
- **Window size and position are always persisted.**

### Where they live in this repo

| Rule | Code |
|---|---|
| Theme choice | `RizzServer.App/ThemeService.cs`, `UiSettings.cs` (`ThemeMode`), View menu in `Views/MainWindow.axaml` |
| Light/Dark palettes + blue accent | `RizzServer.App/App.axaml` (theme dictionaries; always use `DynamicResource` for brushes) |
| Size/position persistence | `WindowPlacementService.cs` (attach to every resizable window) + pane sizes in `Views/MainWindow.axaml.cs`; stored in `ui-settings.json` next to the project file |
| About window | `Views/AboutWindow.axaml(.cs)`, icon in `Assets/` |

When adding a window: call `WindowPlacementService.Attach(window, "<unique key>")`. When adding colours: add a Light and a Dark value
to the theme dictionaries in `App.axaml` instead of hard-coding hex values in views.

## Testing rule

Headless runs, smoke tests and screenshots **must set `RIZZSERVER_DATA_DIR` to a temp folder** before touching `ConfigStore`/`UiSettings`.
Setting the `APPDATA` environment variable does **not** redirect `Environment.GetFolderPath(ApplicationData)` on Windows, so without
`RIZZSERVER_DATA_DIR` a test writes into the user's real `%AppData%\RizzServer` (their project, UI settings, certificates).

Service tests: start the real `rizzserver-service` process with `RIZZSERVER_DATA_DIR` (and `RIZZSERVER_API_URLS`) set, use a non-default OPC UA port (4840 may be taken by another app), and always kill leftover processes. Do **not** enable `InvariantGlobalization` in the service: the OPC UA stack fails to start with it.

## Project layout

- `RizzServer.Core` – models, `GatewayEngine`, driver catalog, CSV import
- `RizzServer.Drivers` – protocol drivers (Modbus, RS-232 variants, MQTT, OPC DA, OPC UA client)
- `RizzServer.Server` – OPC UA server (OPC Foundation stack pinned to 1.5.378.x) and `GatewayRuntime` (config + engine + OPC UA host, shared by the UI and the service)
- `RizzServer.App` – Avalonia UI
- `RizzServer.Service` – headless host: REST API (`/api/v1`, minimal APIs), API-key auth, Windows/systemd service support. New configuration or runtime features should be reachable through the API too, and validated by `Core/ConfigValidator`.
- `installer/` – `build.ps1` (Windows MSI), `publish-portable.ps1` (win/linux/osx archives)

Model: channel (driver + shared link settings) → device (address settings, scan rate) → tags. OPC UA NodeIds are `Channel.Device.Tag`.
