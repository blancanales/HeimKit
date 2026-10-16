# Server setup

Install HeimKit on a Windows Valheim dedicated server using Thunderstore Mod Manager or a manual installation. Both routes use the same plugin and feature configuration. See the [InfiniteTorches guide](features/infinite-torches.md) and [WeatherGuard guide](features/weather-guard.md) and [AFKNightWatch guide](features/afk-night-watch.md) and [AFKRaidWatch guide](features/afk-raid-watch.md) for feature-specific usage.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit build | 0.1.51 |
| Valheim Dedicated Server | 1.0.20; built-in audited version. See the [hidden version override](#hidden-version-override) for an operator-selected requirement. |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Build output | `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll` |

This section is the sole documentation source for HeimKit's supported game, loader, runtime and framework versions. Other documents link here rather than repeating version values. Update this baseline when code guards and dependencies change.

## Installation

### Before either installation route

1. Stop the dedicated server and [back up its world/save data](world-backups.md) before installing or enabling HeimKit. Also preserve any existing loader/configuration files.
2. Locate the dedicated-server folder containing `valheim_server.exe`. This is separate from the client game's `Valheim` folder. For a Steam installation, open **Valheim Dedicated Server > Properties > Installed Files > Browse**.
3. Obtain `HeimKit.dll` from the project, or follow [building from source](development.md#building-from-source). These instructions accept a local DLL and do not require a published HeimKit Thunderstore package.
4. Use the game and loader versions in [Supported environment](#supported-environment). Inspect an existing BepInEx installation before replacing its files.

Choose one route below for the server. Having Thunderstore Mod Manager installed does not by itself install BepInEx or HeimKit into the dedicated-server process. HeimKit belongs on the server; client installation is not required by the design.

### Option A: Thunderstore Mod Manager

This route keeps HeimKit in a manager profile and launches the dedicated server through that profile. Thunderstore supports Valheim profiles and mod installation; the [Valheim modding maintainers' server guidance](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman) covers dedicated-server launching and argument forwarding.

1. Install [Thunderstore Mod Manager](https://www.overwolf.com/app/thunderstore-thunderstore_mod_manager), or open it if already installed. Select Valheim and its server mode. Use a separate server profile, such as **HeimKit Server**, rather than sharing a running client profile.
2. Set the dedicated-server location to the folder containing `valheim_server.exe`. Use the manager's server launch mode, not the normal game launch. If your manager installation does not expose server mode or a server location, use [Option B](#option-b-manual-installation) instead.
3. Install **denikson - BepInExPack_Valheim** into the selected server profile, using the version in the supported environment above. If the loader is already present, retain its configuration rather than replacing it blindly.
4. Open the selected profile's **Settings > Browse profile folder**. Inside that profile, create `BepInEx/plugins/HeimKit/` and copy `HeimKit.dll` there. Keep one HeimKit DLL in the profile; remove duplicate older copies from other plugin folders. This manual DLL addition works alongside manager-installed dependencies. Profile-folder navigation is described in the [Thunderstore profile documentation](https://wiki.thunderstore.io/sharing-your-mods/modpacks-and-profiles).
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

### Hidden version override

After reviewing compatibility for a game update, an operator can manually add this shared setting to `heimkit.cfg`:

```ini
[Compatibility]
SupportedValheimVersion = <exact-game-version>
```

Replace `<exact-game-version>` with the exact Valheim version being authorized, using the three numeric components described below. The built-in audited baseline is listed only in [Supported environment](#supported-environment). This replaces the built-in version requirement for all four modules; it is not a list or an additional allowed version. Arrays and comma-separated lists are not accepted: a dedicated-server installation runs one game version at a time. Each module still honors its own Enabled setting. The setting and section are absent from generated defaults and the reference config. Existing manual values are preserved on save, and the optional shared section is grouped before feature sections.

The value must contain exactly three nonnegative decimal components in canonical `major.minor.patch` form, without leading zeros, suffixes, wildcards or version ranges. Surrounding whitespace is ignored. Missing values use the built-in audited version; empty, malformed or unreadable values produce one shared startup warning and fall back to it. A different valid requirement produces one shared warning stating the manual requirement and built-in audited version. The override does not establish API or gameplay compatibility, alter Valheim's client/server network checks, or bypass runtime method/signature, prefab, world-readiness or failure checks. Remove the entry to restore the built-in requirement. Stop the server before editing and restart to apply changes; the policy is captured once per loaded configuration.

### Default configuration

Shared settings (`[Diagnostics]` and `[PlayerActivity]`) appear first; feature sections follow, with no required order among features. HeimKit uses the configuration directory supplied by BepInEx. Existing values are preserved, and missing settings are added with defaults. The supplied [config/heimkit.cfg](../config/heimkit.cfg) is an optional reference; do not overwrite an existing server config with it.

```ini
[PlayerActivity]
IdleTimeoutSeconds = 300
JoinGraceSeconds = 60
MovementThresholdMeters = 0.5
LookThresholdDegrees = 10
SampleIntervalSeconds = 1

[Diagnostics]
DebugMode = false

[Torches]
DebugMode = false
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch,piece_brazierceiling01,piece_brazierfloor01,piece_brazierfloor02

[WeatherGuard]
DebugMode = false
Enabled = true
ProtectRain = true
ProtectUnderwater = true
ExcludedPrefabs =
DebugLogLevel = Detailed
LogWorldCreatedItems = false

[AFKNightWatch]
DebugMode = false
Enabled = true
MajoritySleepEnabled = true
ExcludeAFKFromSleep = true
RequiredSleepingPercent = 50
RequireMoreThanPercent = true
SleepDelaySeconds = 30

[AFKRaidWatch]
Enabled = true
DebugMode = false
```

Each feature has an independent Enabled switch. Stop the server, edit the appropriate value, save and restart. Startup reports each feature's configured Enabled or Disabled state. An enabled feature then reports Ready when initialization succeeds, or a clear issue if it cannot initialize. Normal waiting and technical details appear only at Debug. Unsupported game versions disable the affected module.

Global `[Diagnostics] DebugMode` enables bounded diagnostics across all features. Each feature also has a visible DebugMode setting, default false, in its own section. Startup adds any missing global or feature DebugMode keys to the active heimkit.cfg with that default, including disabled features, and preserves existing values. The hidden DisableDebugLogging key remains manual-only and is never added just because it is missing. With global debug off, set only the desired feature’s DebugMode to true. A false feature setting inherits global debug; it does not suppress global output. Startup, readiness, warnings and errors remain visible regardless. Optional diagnostic messages use BepInEx Debug severity; ordinary lifecycle messages and explicitly requested coverage reports retain Info severity. The [logging contract](architecture.md#logging) uses concise `Feature: Message` output for Info/Warning and structured evidence for Debug/Error/exception details. Its [repetition limits](architecture.md#repetition-and-volume-limits) keep unchanged status, empty scopes and repeated incidents from flooding logs.

When any debug switch or hidden suppression is requested, each affected feature emits one startup Debug policy entry naming the source and effective configuration, including disabled features. A suppressed feature still emits this one declaration to explain its override; recurring diagnostics remain suppressed. All-off configurations stay quiet. These entries still require the loader's Debug filter.

To display optional diagnostics, stop the server and add `Debug` to the existing `LogLevels` list under `[Logging.Console]` and/or `[Logging.Disk]` in the active loader's `BepInEx/config/BepInEx.cfg`, then restart. Console controls console output; Disk controls `LogOutput.log`. Preserve the other enabled levels, for example `LogLevels = Fatal, Error, Warning, Message, Info, Debug`. HeimKit's effective DebugMode must also be enabled. HeimKit does not alter BepInEx's log filters automatically.

Automatic Debug output across all features pauses after five minutes with no connected players and resumes when a player connects. The same grace period allows startup diagnostics before anyone joins. This pause does not change DebugMode settings or affect feature operation, normal status, warnings, errors or requested coverage reports. Existing sampling limits still apply; see the [shared activity rule](architecture.md#empty-server-debug-pause).

### AFKNightWatch

AFKNightWatch owns majority sleep and idle exclusion from sleep. Enabled controls sleep; MajoritySleepEnabled and ExcludeAFKFromSleep select its two independent behaviors. The default threshold is strictly more than half after exclusion: one of two participants is insufficient. Set RequiredSleepingPercent to 100 for unanimity, or RequireMoreThanPercent false for an inclusive threshold. With majority off, every non-idle participant must sleep. With both behaviors off, native voting applies. SleepDelaySeconds defaults to 30 real seconds (0–300): the threshold must remain met throughout the wait while some players are not in bed. If everyone reaches bed, sleep may start immediately. Losing quorum or reliable observations restarts the wait. Modified votes require a known living bed sleeper and preserve native afternoon/night, cooldown and bed requirements. See the [guide](features/afk-night-watch.md) and [specification](specs/afk-night-watch.md).

### AFKRaidWatch

AFKRaidWatch's independent Enabled switch protects idle-only local areas from new normal automatic random raids. Active or uncertain nearby actors keep native eligibility. Existing, explicit/admin, forced/scripted and standalone events retain normal behavior. There is no second protection switch. See the [guide](features/afk-raid-watch.md) and [specification](specs/afk-raid-watch.md).

### PlayerActivity

These settings control how HeimKit decides whether a player appears idle. AFK sleep exclusion and raid protection share the same detection. They do not kick players or announce an AFK status. Stop the server before changing them and restart to apply the changes.

| Setting | Default | What it does and how to adjust it | Allowed values |
| --- | --- | --- | --- |
| `IdleTimeoutSeconds` | 300 seconds (5 minutes) | How long a player must go without activity the server can detect before being considered idle. Lower values mark players idle sooner; higher values wait longer. | 30–3600 seconds |
| `JoinGraceSeconds` | 60 seconds (1 minute) | Minimum time a player is kept active after joining or recovering from loading, death or unreliable player information. Increase it to allow more settling-in time; decrease it to shorten that minimum. The grace and idle timeout must both have passed; they are not added together. | 0–600 seconds |
| `MovementThresholdMeters` | 0.5 meters | How far the player's position must change from the last activity position to count as movement. Small position changes can build up to the threshold. Lower values pick up smaller movements; higher values ignore more small movements. Being carried or moved by the world also counts. | 0.1–5 meters |
| `LookThresholdDegrees` | 10 degrees | How much the character must turn to count as activity. Lower values detect smaller turns; higher values require larger turns. This uses character direction reported to the server, so moving only the camera may not count. | 1–90 degrees |
| `SampleIntervalSeconds` | 1 second | How often HeimKit checks player state for activity. Lower values check more often and use more server processing; higher values check less often and may delay detecting changes. | 0.2–5 seconds |

For example, with the defaults a newly joined player who shows no activity is not considered idle after one minute: the five-minute idle timeout must also pass. Normal activity restarts the idle timer.

Idle is an estimate based on what the server can see. Some local camera, menu and inventory actions are invisible. Loading, unknown/dead data and observation recovery restart the grace period. Detection continues during the day whenever sleep exclusion or raid protection needs it. Majority sleep alone does not need idle detection. See the [shared-service contract](architecture.md#player-activity) for implementation details.

This is a fresh configuration layout. HeimKit does not convert legacy sections, read legacy aliases or automatically delete them. Recognized current values are preserved and missing current settings are added. For a clean configuration, with the server stopped the operator can remove the active heimkit.cfg and allow the next startup to generate complete defaults, then reapply desired settings. The plugin does not delete the server configuration. Unbound legacy entries may remain if an old file is retained, but do not control these features.

All four features use shared presence notifications to pause their gameplay controllers when empty or player presence is unavailable, then resume after the first player has spawned in the world, subject to native world readiness. Additional logins do not restart them. WeatherGuard releases retained ownership and protection hooks during the pause; explicitly requested coverage reports remain available separately. See the [required lifecycle hookup and exceptions](architecture.md#required-feature-hookup).

Normal Info logs distinguish transport connections from world entry. PlayerPresence reports all features inactive after confirmed empty-server cleanup, and all enabled features active after the first joined player and successful feature initialization. Delayed readiness defers the active notice. Incoming connections and completed login handshakes alone do not resume gameplay features. Death/respawn keeps the same session joined until disconnect. After every enabled consumer confirms its own cleanup, one shared entry reports `PlayerPresence: No players have joined the world; all features are inactive until a player joins.` A cleanup failure remains visible and suppresses this combined success notice for that empty interval. Each enabled feature reports Ready once when its required dedicated-server services are available, regardless of players. The shared presence status follows those Ready entries. Individual processing transitions, including other pause reasons and later starts, belong only in Debug; warnings/errors remain visible. Additional players and unchanged polls do not repeat these notices. Unavailable presence is not reported as an empty server. See the [presence lifecycle](architecture.md#presence-driven-lifecycle).

Shared PlayerActivity debug output has no additional switch. It uses one transition stream permitted by any registered consumer's effective global/feature/hidden policy. This does not enable the other consumer's feature-specific output. Required gameplay inference is independent of diagnostics.

### Hidden per-feature debug suppression

Global debug and a feature’s own DebugMode enable diagnostics together; hidden suppression has final precedence. An optional `DisableDebugLogging` key can be added manually to an existing feature section (`[Torches]`, `[WeatherGuard]`, `[AFKNightWatch]`, `[AFKRaidWatch]`). The plugin does not generate this key or include it in the reference config. It preserves manually supplied values when adding defaults or saving configuration.

| Global DebugMode | Feature DebugMode | Hidden DisableDebugLogging | Feature diagnostics |
| --- | --- | --- | --- |
| false | false | Missing or false | Off |
| false | true | Missing or false | On for this feature |
| true | Either | Missing or false | On |
| Either | Either | true | Off |
| Either | Either | Invalid | Off, with a warning |

The effective rule is `(global DebugMode OR feature DebugMode) AND NOT DisableDebugLogging`. To diagnose WeatherGuard alone, set global DebugMode false, WeatherGuard DebugMode true, and the other feature DebugMode values false. Ensure WeatherGuard’s hidden override is missing or false. Stop the server before configuration edits and restart afterward.

For example, to keep WeatherGuard diagnostics while suppressing torch diagnostics, manually add this under the existing torch section:

```ini
[Torches]
DisableDebugLogging = true
```

The key suppresses that feature's recurring optional output and diagnostic collection; the one startup Debug policy declaration remains available to explain suppression. It cannot force diagnostics on, disable gameplay, hide startup/readiness messages or suppress warnings/errors. Features remain independent. WeatherGuard's Medium/Detailed selection, optional world-item scope and all sample limits still apply when its effective diagnostics are enabled. Removing the key or setting it to false restores the global-or-feature rule.

Use `true` or `false` (case-insensitive). Empty strings, other values and an unreadable override use the safe fallback of suppressing that feature's diagnostics. One warning per feature per loaded configuration identifies the section, key and fallback without echoing the supplied text. Invalid manual values are retained rather than silently rewritten. Section and key names are case-sensitive. Inline comments on the value are not supported.

Stop the server before editing and restart to apply changes. Overrides are resolved once per loaded configuration; runtime file edits or config reloads are not advertised as applying them. Retired diagnostic category settings are removed independently and never interpreted as feature overrides.

### Requested WeatherGuard coverage

A server operator with write access to the active loader configuration directory can request a coverage report without enabling debug. Create an empty file named `heimkit-weatherguard-coverage.request` beside the active `heimkit.cfg`. Use the profile configuration directory for a manager installation or the dedicated-server configuration directory for a manual installation; the configuration path is logged at startup. PowerShell command, replacing the example directory with that active location:

```powershell
New-Item -ItemType File -Path 'C:\path\to\active\BepInEx\config\heimkit-weatherguard-coverage.request'
```

File Explorer can create the same empty file; include the `.request` extension rather than `.request.txt`. HeimKit polls at most once per second, consumes the file and writes one readable Info coverage report to the active server log. A new file requests a new report; requests made before a poll coalesce. No configuration restart is required to request the report. The server process must have permission to delete the request file. Access failures warn once until a request is successfully consumed; report failures produce an error without changing feature processing.

This is a local server-administration mechanism authorized by filesystem access, not an in-game client command or public network endpoint. Inspection of the supported game's `Terminal.TryRunCommand` and `ZNet.RPC_RemoteCommand` shows that an unmodified client does not recognize a new custom command to forward to the server. The dedicated server's game console is graphical; this mechanism does not assume standard-input command support or repurpose vanilla gameplay commands.

The report remains available when global debug is off, the feature's hidden debug suppression is on, or WeatherGuard is disabled. It never enables automatic diagnostics. See the [WeatherGuard guide](features/weather-guard.md#coverage-report) for scope and count interpretation. Module-inactive or unobserved scope reports unknown counts rather than claiming protection over unloaded or unseen world areas.

## Troubleshooting and removal

If the plugin does not load, inspect `BepInEx/LogOutput.log` in the active loader location. For Option A, use the server profile's files and the manager's server launch; for Option B, use the dedicated-server folder's files and its startup script. Version warnings require API review before changing the guard. Logs report transmission attempts, not confirmed delivery. Remove credentials and join codes before sharing logs.

To disable a module, stop the server, set its Enabled value to false in the active `heimkit.cfg` and restart using the same launch route. To remove HeimKit, stop the server and remove its DLL from the active profile or manual server installation. Torch fuel remains ordinary saved data. WeatherGuard releases network ownership and does not persist a new protection flag or creator; existing custom world fields from other implementations are not automatically removed.

The shared AFK detector logs individual AFK entry and exit at Info, including the character name/peer ID and current AFK count out of joined players. Debugging is not required. Notices appear only while an enabled feature requires AFK inference. See the [AFK notice contract](architecture.md#ordinary-afk-notices) for count scope and tracking-loss behavior.

When feature diagnostics are configured on, startup checks the currently registered native disk and console listeners. A known listener excluding Debug produces one visible feature Warning identifying its LogLevels setting, immediately followed by Info messages explaining the independent loader filter, the config file and each affected section with a LogLevels example, preservation of existing levels and the required restart. The explanation also states that feature DebugMode can operate with global HeimKit DebugMode off. Intentional hidden suppression does not produce a delivery warning. The check does not alter loader settings or infer the behavior of unknown/custom listeners.
