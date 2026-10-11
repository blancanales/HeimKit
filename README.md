# HeimKit

Infinite torches for Windows Valheim dedicated servers, built with C# and BepInEx. The source also contains WeatherGuard server processing for player-built structures, awaiting in-game verification.

**Windows dedicated servers only.** Unmodified clients are the intended design; Linux is not supported.

Wooden-torch refilling and lighting passed an initial live test. WeatherGuard builds and passes local checks; its rain, combat, support and ownership-handover behavior still needs a live test. There is no production release advertised.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit build | 0.1.11 |
| Valheim Dedicated Server | 1.0.17; enforced by module compatibility guards. |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Local check runner | .NET 10 SDK |

These are the project's audited game/loader baseline and build requirements. Feature gameplay validation is stated separately above; a compatibility guard passing does not establish successful gameplay. Update version support here when code guards and dependencies change.

## Installation

1. Stop the dedicated server and back up its world/save data. Use a test world for WeatherGuard verification.
2. Install the [Valheim BepInEx pack](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) beside `valheim_server.exe` if it is not already installed. Inspect an existing loader before replacing it.
3. Copy `HeimKit.dll` into `BepInEx/plugins/HeimKit/`. Remove duplicate older HeimKit DLLs from other plugin directories.
4. Start the server normally. HeimKit creates `BepInEx/config/heimkit.cfg` automatically and logs its full path. No manual config-file creation is required.
5. Read `BepInEx/LogOutput.log` for module startup, readiness, warnings and errors. Stop the server before replacing the DLL or editing configuration.

Install the plugin on the dedicated server. Client installation is not required by the design.

## Configuration

HeimKit uses the configuration directory supplied by BepInEx. Existing values are preserved, and missing settings are added with defaults. The supplied [config/heimkit.cfg](config/heimkit.cfg) is an optional reference; do not overwrite an existing server config with it.

```ini
[Torches]
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch

[WeatherGuard]
Enabled = false

[Diagnostics]
DebugMode = false
```

Each feature has an independent Enabled switch. Stop the server, edit the appropriate value, save and restart. Startup reports ENABLED or DISABLED and any compatibility reason. Unsupported game versions disable the affected module.

DebugMode is the single troubleshooting switch. When false, logs contain general configuration, startup, readiness, warnings and errors. When true, they also contain bounded diagnostic details and status summaries. Diagnostic messages use Info level so normal BepInEx log filters display them.

## Infinite Torches

Eligible low-fuel torches receive the vanilla fuel-setting message, targeting their normal maximum fuel without spending player inventory materials. Requests begin below 75% capacity and are limited by MaxMessagesPerPass. Pending object tracking suppresses duplicate requests and allows retries after unresolved timeouts.

The default allowlist covers wooden, iron, green, blue and wall torches. Unsupported prefabs are warned about and skipped. Unloaded objects and ownership changes can delay refilling. Normal water, smoke and obstruction behavior remains; a fueled torch is not guaranteed to stay lit in every environment. Simultaneous manual refilling can still consume resin.

Debug output includes examined candidates, sent requests, skip reasons, pending records and deferred candidates. Status summaries appear at most once per minute; detailed refill requests are capped at four per minute. A sent request or observed fuel increase is not a delivery acknowledgement.

The [Infinite Torches specification](docs/specs/infinite-torches.md) describes the implemented selection, networking, duplicate suppression and limits.

## WeatherGuard testing

WeatherGuard's main purpose is automatic protection for existing and newly player-built structures, preserving their creators. The current code takes network ownership of eligible structural pieces, loads surrounding terrain/objects and rejects their client state updates. Combat, repair, removal and support use native server code. World-created claiming is inactive.

Enable WeatherGuard and DebugMode for the test. Torches can be disabled independently to keep the logs focused:

```ini
[Torches]
Enabled = false

[WeatherGuard]
Enabled = true

[Diagnostics]
DebugMode = true
```

1. Start the test server with the current DLL. Confirm `Loading [HeimKit 0.1.11]`, `WeatherGuard: ENABLED` and `WeatherGuard: ready`.
2. Join with a vanilla client and stay connected. Go to an existing exposed player-built wooden structure. No repair is needed. Look for `WeatherGuard: server control active for ...` and acquisition samples. Readiness alone does not establish protection.
3. Build a new exposed wooden half wall, pole or floor nearby. It should be acquired automatically without repair, logout or manual refresh.
4. Compare it with a nearby unrepaired world-generated wooden piece during natural rain for 3–5 minutes. Both must be exposed, loaded and initially above 50% health, because normal rain wear stops at half health. The player-built piece should retain health while the world piece loses health. If neither changes, verify rain/exposure, loading, shields and starting health. Newly loaded wear can have a warmup delay.
5. Hit the protected piece, repair it and hit it again. Ordinary damage and repair should work. Include actions during acquisition when checking for missed hits or delayed changes.
6. On disposable player-built pieces, test hammer removal and loss of structural support. Removal should work normally, and unsupported pieces should collapse after the normal loading warmup. Check resource drops, any doors, and nearby chest opening/item transfers too.
7. Send the complete log from startup through these actions, along with what happened to target/control health and support. One player is sufficient for this initial test; multiplayer, movement, restart/reacquisition and server performance require separate verification before support claims expand.

Protection can wait for server context loading. The scope is static player-built structural categories with rain/wetness wear; containers, moving objects, non-building categories and already rain-immune materials are excluded. The flag also suppresses underwater wetness wear. Snow, Ashlands/lava and other biome hazards are not suppressed.

The server loads extra processing context. Current bounds are eight distinct character-centered regions, 32,768 retained context records and 8,192 protected pieces. A capacity warning means unmanaged pieces remain vanilla. See the [WeatherGuard specification](docs/specs/weather-guard.md) for the implemented behavior and limits.

### WeatherGuard diagnostics

Debug summaries appear about once per minute:

| Log field | Meaning |
| --- | --- |
| protected / eligible | Currently server-controlled pieces and qualifying nearby pieces. |
| waiting terrain / missing instances / waiting surrounding objects | Reasons acquisition is delayed. |
| acquired / released | Cumulative network-ownership transitions. |
| blocked client ZDO records | Rejected state records or sector invalidations for protected IDs. |
| prevented owner transfers | Local reassignment attempts held at the server. |
| blocked client broadcasts | Rejected client processing/destruction messages. |
| owner commands routed | Requests redirected from an old owner target to the server. |
| protected UpdateWear calls | Invocations under protection, not proof of rain prevention. |

Acquisition samples are capped at six per minute. If eligible stays nonzero but protected remains zero, include the waiting counts. If WeatherGuard reports DISABLED after an error, stop the test and provide the full exception; the module attempts to release pieces to vanilla ownership.

## Building from source

Run from the repository root with local Windows server assemblies and a .NET SDK:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

If the BepInEx development references are elsewhere:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

Output is `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll`. Install only that DLL; the adjacent example config is optional. Game and BepInEx assemblies are not distributed. WeatherGuard uses the Harmony library supplied with BepInEx; Jotunn is not required.

Run the local check runner with a .NET 10 SDK:

```powershell
dotnet run --project .\tests\HeimKit -c Release '-p:ServerPath=C:\path\to\Valheim dedicated server'
```

Add `-p:BepInExCore` when needed. The checks exercise configuration, lifecycle, diagnostics, pending refills, protection policy and packet filtering. They do not run a Unity world or establish live networking/gameplay behavior.

## Troubleshooting and removal

If the plugin does not load, verify BepInEx is beside the dedicated-server executable and inspect its log. Version warnings require API review before changing the guard. For refill problems, include startup logs, two diagnostic intervals and visible fuel/light behavior. Logs report transmission attempts, not confirmed delivery. Remove credentials and join codes before sharing logs.

To disable a module, stop the server, set its Enabled value to false and restart. To remove HeimKit, stop the server and remove its DLL. Torch fuel remains ordinary saved data. WeatherGuard releases network ownership and does not persist a new protection flag or creator; existing custom world fields from other implementations are not automatically removed.

## Project documentation

[Contributing](docs/contributing.md) explains how to create, organize and validate features. [Architecture](docs/architecture.md) defines placement, naming, configuration and lifecycle rules. Each feature has one specification describing its current code. These documents contain no development journals, release-by-release histories or completion checklists. Installation, testing and troubleshooting instructions belong in this README. Git retains source history.

[AGENTS.md](AGENTS.md) contains contributor instructions and points to the project rules. Keep code changes focused, preserve independent feature controls, and update the relevant current specification when behavior changes. Do not commit generated output, proprietary assemblies, runtime files, worlds, credentials or machine-specific paths. Commit and push when requested.

## License

HeimKit uses the [MIT License](LICENSE). Game files and dependencies retain their own licenses and are not distributed or relicensed by this project.
