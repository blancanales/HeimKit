# Server setup

Install HeimKit on a Windows Valheim dedicated server using Thunderstore Mod Manager or a manual installation. Both routes use the same plugin and feature configuration. See the [Infinite Torches guide](features/infinite-torches.md) and [WeatherGuard guide](features/weather-guard.md) for feature-specific usage.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit build | 0.1.20 |
| Valheim Dedicated Server | 1.0.17; enforced by module compatibility guards. |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Build output | `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll` |

This section is the sole documentation source for HeimKit's supported game, loader, runtime and framework versions. Other documents link here rather than repeating version values. Update this baseline when code guards and dependencies change.

## Installation

### Before either installation route

1. Stop the dedicated server and [back up its world/save data](world-backups.md) before installing or enabling HeimKit. Also preserve any existing loader/configuration files.
2. Locate the dedicated-server folder containing `valheim_server.exe`. This is separate from the client game's `Valheim` folder. For a Steam installation, open **Valheim Dedicated Server â†’ Properties â†’ Installed Files â†’ Browse**.
3. Obtain `HeimKit.dll` from the project, or follow [building from source](development.md#building-from-source). These instructions accept a local DLL and do not require a published HeimKit Thunderstore package.
4. Use the game and loader versions in [Supported environment](#supported-environment). Inspect an existing BepInEx installation before replacing its files.

Choose one route below for the server. Having Thunderstore Mod Manager installed does not by itself install BepInEx or HeimKit into the dedicated-server process. HeimKit belongs on the server; client installation is not required by the design.

### Option A: Thunderstore Mod Manager

This route keeps HeimKit in a manager profile and launches the dedicated server through that profile. Thunderstore supports Valheim profiles and mod installation; the [Valheim modding maintainers' server guidance](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman) covers dedicated-server launching and argument forwarding.

1. Install [Thunderstore Mod Manager](https://www.overwolf.com/app/thunderstore-thunderstore_mod_manager), or open it if already installed. Select Valheim and its server mode. Use a separate server profile, such as **HeimKit Server**, rather than sharing a running client profile.
2. Set the dedicated-server location to the folder containing `valheim_server.exe`. Use the manager's server launch mode, not the normal game launch. If your manager installation does not expose server mode or a server location, use [Option B](#option-b-manual-installation) instead.
3. Install **denikson â€” BepInExPack_Valheim** into the selected server profile, using the version in the supported environment above. If the loader is already present, retain its configuration rather than replacing it blindly.
4. Open the selected profile's **Settings â†’ Browse profile folder**. Inside that profile, create `BepInEx/plugins/HeimKit/` and copy `HeimKit.dll` there. Keep one HeimKit DLL in the profile; remove duplicate older copies from other plugin folders. This manual DLL addition works alongside manager-installed dependencies. Profile-folder navigation is described in the [Thunderstore profile documentation](https://wiki.thunderstore.io/sharing-your-mods/modpacks-and-profiles).
5. In the dedicated-server folder, back up `start_headless_server.bat`. At the end of its existing `valheim_server` launch line, append a space followed by `%*` if it is not already present. Keep the existing server name, world, password, port and other arguments. This forwards the manager's loader arguments, as documented by the Valheim modding maintainers.
6. Start the dedicated server using the manager's modded server launch action. Use this profile for subsequent starts. If you also play on the same machine, use a separate client profile or launch the unmodified client normally.

The active loader, plugin and configuration files for this route are in the selected **profile's** `BepInEx` folder. A DLL copied only into the Steam server folder is not the same as one installed into the active manager profile. Menu labels can vary between manager versions; use the manual route if the dedicated-server launch options are unavailable.

### Option B: Manual installation

This route puts the loader and HeimKit directly beside the dedicated-server executable. It also applies when Thunderstore Mod Manager is already installed: leave the manager profile separate and launch this server with its own startup script.

1. Download **BepInExPack_Valheim** from its [package page](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) using the version listed above. Extract the archive into a temporary folder.
2. Copy the **contents** of `BepInExPack_Valheim` into the dedicated-server folder containing `valheim_server.exe`. Copy the loader's root files as well as its `BepInEx` folder; the archive's outer folder must not become an extra nested directory. For an existing installation, preserve configuration and update only the loader files needed. The pack's [manual installation instructions](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/#installation-manual) describe the archive layout; for HeimKit the destination is the dedicated-server folder.
3. Create `BepInEx/plugins/HeimKit/` in that server folder and copy `HeimKit.dll` into it. Keep one HeimKit DLL; remove duplicate older copies from other plugin folders in this server installation.
4. Start the Windows dedicated server with its own startup script, such as `start_headless_server.bat`, keeping its existing world and connection settings. Use this launch route for later starts rather than switching to a manager profile's loader arguments.

The active loader, plugin and configuration files for this route are in the **dedicated-server folder's** `BepInEx` directory. Installing Thunderstore Mod Manager afterward does not move or manage this manual installation automatically.

### Shared configuration and maintenance

On startup, HeimKit creates `BepInEx/config/heimkit.cfg` under the active loader location and logs the full configuration path. There is no need to create or copy a configuration file first. Use the profile location for Option A and the dedicated-server location for Option B.

**Updating or replacing the mod while the dedicated server is running can corrupt the world.** Stop the server gracefully and wait for it to finish saving and fully exit before changing the DLL or applying manager updates. Make a [world backup](world-backups.md) before updating, as well as before the initial installation.

With the server stopped, replace the DLL in the same active location used during installation, preserve `heimkit.cfg`, and start with the same launch route. Stop the server before editing configuration too. A manually added DLL must be replaced manually; installing the manager does not enable automatic updates for that file.

## Configuration

HeimKit uses the configuration directory supplied by BepInEx. Existing values are preserved, and missing settings are added with defaults. The supplied [config/heimkit.cfg](../config/heimkit.cfg) is an optional reference; do not overwrite an existing server config with it.

```ini
[Torches]
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch

[WeatherGuard]
Enabled = false
ProtectRain = true
ProtectUnderwater = true
ExcludedPrefabs =
DebugLogLevel = Medium
LogWorldCreatedItems = false

[MajoritySleep]
Enabled = true
RequiredSleepingPercent = 50
RequireMoreThanPercent = true

[AfkSleepExclusion]
Enabled = true

[AfkRaidProtection]
Enabled = true

[PlayerActivity]
IdleTimeoutSeconds = 300
JoinGraceSeconds = 60
MovementThresholdMeters = 0.5
LookThresholdDegrees = 10
SampleIntervalSeconds = 1

[Diagnostics]
DebugMode = false
```

Each feature has an independent Enabled switch. Stop the server, edit the appropriate value, save and restart. Startup reports ENABLED or DISABLED and any compatibility reason. Unsupported game versions disable the affected module.

DebugMode is the single troubleshooting switch. When false, logs contain general configuration, startup, readiness, warnings and errors. When true, they also contain bounded diagnostic details and status summaries. Diagnostic messages use Info level so normal BepInEx log filters display them.

### Player activity and sleep

MajoritySleep, AfkSleepExclusion and AfkRaidProtection each have an independent Enabled switch. The default sleep threshold is strictly more than 50 percent after optional idle exclusion; one of two is insufficient. Set RequiredSleepingPercent to 100 for unanimity, or RequireMoreThanPercent to false for an inclusive threshold. Without majority sleep, AFK exclusion requires every non-idle player to sleep. With both sleep modules disabled, vanilla voting applies. All modified votes require at least one living bed sleeper.

PlayerActivity settings apply to both AFK consumers: IdleTimeoutSeconds defaults to 300 (five minutes), JoinGraceSeconds to 60, MovementThresholdMeters to 0.5, LookThresholdDegrees to 10 and SampleIntervalSeconds to 1. Allowed ranges are 30–3600 seconds, 0–600 seconds, 0.1–5 meters, 1–90 degrees and 0.2–5 seconds respectively. Position and body rotation are replicated evidence; local camera/input and menu actions are not all visible. Station/container use holds active when observable. Unknown/loading/death recovery resets idle inference. See the [activity contract](architecture.md#shared-player-activity) for limits and the [sleep](features/majority-sleep.md), [sleep exclusion](features/afk-sleep-exclusion.md) and [raid](features/afk-raid-protection.md) guides for behavior. Stop the server before edits and restart to apply them.

### Hidden per-feature debug suppression

Global `[Diagnostics] DebugMode` remains the master switch. An optional `DisableDebugLogging` key can be added manually to an existing feature section (`[Torches]`, `[WeatherGuard]`, `[MajoritySleep]`, `[AfkSleepExclusion]` or `[AfkRaidProtection]`). The plugin does not generate this key or include it in the reference config. It preserves manually supplied values when adding defaults or saving configuration.

| Global DebugMode | Feature DisableDebugLogging | Feature diagnostics |
| --- | --- | --- |
| false | Missing, false or true | Off |
| true | Missing or false | On |
| true | true | Off |
| Either | Invalid | Off, with a warning |

For example, to keep WeatherGuard diagnostics while suppressing torch diagnostics, manually add this under the existing torch section:

```ini
[Torches]
DisableDebugLogging = true
```

The key only suppresses that feature's optional output and diagnostic collection. It cannot force diagnostics on, disable gameplay, hide startup/readiness messages or suppress warnings/errors. Features remain independent. WeatherGuard's Medium/Detailed selection, optional world-item scope and all sample limits still apply when its effective diagnostics are enabled. Removing the key or setting it to false restores global inheritance.

Use `true` or `false` (case-insensitive). Empty strings, other values and an unreadable override use the safe fallback of suppressing that feature's diagnostics. One warning per feature per loaded configuration identifies the section, key and fallback without echoing the supplied text. Invalid manual values are retained rather than silently rewritten. Section and key names are case-sensitive. Inline comments on the value are not supported.

Stop the server before editing and restart to apply changes. Overrides are resolved once per loaded configuration; runtime file edits or config reloads are not advertised as applying them. Retired diagnostic category settings are removed independently and never interpreted as feature overrides.

### Requested WeatherGuard coverage

A server operator with write access to the active loader configuration directory can request a coverage report without enabling debug. Create an empty file named `heimkit-weatherguard-coverage.request` beside the active `heimkit.cfg`. Use the profile configuration directory for a manager installation or the dedicated-server configuration directory for a manual installation; the configuration path is logged at startup. PowerShell command, replacing the example directory with that active location:

```powershell
New-Item -ItemType File -Path 'C:\path\to\active\BepInEx\config\heimkit-weatherguard-coverage.request'
```

File Explorer can create the same empty file; include the `.request` extension rather than `.request.txt`. HeimKit polls at most once per second, consumes the file and writes one Info SUMMARY with `requested=true` to the active server log. A new file requests a new report; requests made before a poll coalesce. No configuration restart is required to request the report. The server process must have permission to delete the request file. Access failures warn once until a request is successfully consumed; report failures produce an error without changing feature processing.

This is a local server-administration mechanism authorized by filesystem access, not an in-game client command or public network endpoint. Inspection of the supported game's `Terminal.TryRunCommand` and `ZNet.RPC_RemoteCommand` shows that an unmodified client does not recognize a new custom command to forward to the server. The dedicated server's game console is graphical; this mechanism does not assume standard-input command support or repurpose vanilla gameplay commands.

The report remains available when global debug is off, the feature's hidden debug suppression is on, or WeatherGuard is disabled. It never enables automatic diagnostics. See the [WeatherGuard guide](features/weather-guard.md#coverage-report) for scope and count interpretation. Module-inactive or unobserved scope reports unknown counts rather than claiming protection over unloaded or unseen world areas.

## Troubleshooting and removal

If the plugin does not load, inspect `BepInEx/LogOutput.log` in the active loader location. For Option A, use the server profile's files and the manager's server launch; for Option B, use the dedicated-server folder's files and its startup script. Version warnings require API review before changing the guard. Logs report transmission attempts, not confirmed delivery. Remove credentials and join codes before sharing logs.

To disable a module, stop the server, set its Enabled value to false in the active `heimkit.cfg` and restart using the same launch route. To remove HeimKit, stop the server and remove its DLL from the active profile or manual server installation. Torch fuel remains ordinary saved data. WeatherGuard releases network ownership and does not persist a new protection flag or creator; existing custom world fields from other implementations are not automatically removed.
