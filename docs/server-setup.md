# Server setup

Install HeimKit on a Windows Valheim dedicated server. Players use unmodified clients. Feature guides cover [torches](features/infinite-torches.md), [weather protection](features/weather-guard.md), [sleep](features/afk-night-watch.md), [raids](features/afk-raid-watch.md) and [SharedMap](features/shared-map.md). Read [safety and limitations](limitations.md) before enabling the mod.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit release baseline | 0.1.55 |
| Valheim Dedicated Server | 1.0.20; built-in audited version. See the [hidden version override](#hidden-version-override) for an operator-selected requirement. |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Build output | `src/HeimKit/bin/<Debug or Release>/netstandard2.1/HeimKit.dll` |

This section is the sole documentation source for HeimKit's supported game, loader, runtime and framework versions. Other documents link here rather than repeating version values. Update this baseline when code guards and dependencies change.

## SharedMap module

SharedMap saves community exploration and shares portal, outdoor-resource, exterior-dungeon, lore-runestone and recurring-spawner markers through vanilla **Read Map**. The module and all five marker options default enabled. Enabled runestone and recurring-spawner markers automatically include eligible points in community-explored terrain; confirmed destroyed spawners are hidden. Resource and dungeon retrospective discovery defaults off; player uploads default allowed. DungeonLootProgress defaults on and can hide pickup percentages independently of DungeonMarkerSync. ResourceMarkerHideDistanceMeters defaults to 10 metres, with zero disabling resource-marker hiding near other generated landmarks. Spawner markers require visible, player-destructible sources; hidden creature-spawn points are excluded. Sharing player X checkmarks defaults off. The [SharedMap guide](features/shared-map.md) owns settings, pin administration and storage details. Include its separate files in [world backups](world-backups.md#sharedmap-data).

## Installation

### Before either installation route

1. Stop the server and [back up the world](world-backups.md), SharedMap data and existing configuration.
2. Locate the dedicated-server folder containing `valheim_server.exe`. In Steam, use **Valheim Dedicated Server > Properties > Installed Files > Browse**.
3. Obtain `HeimKit.dll` or follow [building from source](development.md#building-from-source). Use the versions in [Supported environment](#supported-environment).
4. Keep one HeimKit DLL in the active server installation/profile.

### Option A: Thunderstore Mod Manager

1. Select Valheim's dedicated-server mode and a separate server profile. Set its game path to the folder containing `valheim_server.exe`.
2. Install **denikson - BepInExPack_Valheim** using the supported version. Preserve an existing loader configuration.
3. Open **Settings > Browse profile folder**. Copy `HeimKit.dll` into that profile's `BepInEx/plugins/HeimKit/`.
4. Back up the dedicated server's `start_headless_server.bat`. Append a space and `%*` to its `valheim_server` launch line if absent, preserving existing server/world/password/port arguments. This forwards manager loader arguments.
5. Start through the profile's modded dedicated-server launch action and use that route for later starts.

The active loader and configuration are in the **profile**, not necessarily the Steam server folder. If the manager exposes no dedicated-server mode/path, use manual installation. See the maintainers' [server guidance](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman) and [profile documentation](https://wiki.thunderstore.io/sharing-your-mods/modpacks-and-profiles).

### Option B: Manual installation

1. Download the supported [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and extract it to a temporary folder.
2. Copy the **contents** of its `BepInExPack_Valheim` directory beside `valheim_server.exe`, including loader root files and the `BepInEx` directory. Preserve existing settings.
3. Copy `HeimKit.dll` into `BepInEx/plugins/HeimKit/`.
4. Start the server with its own startup script and existing world/connection settings.

The active files are in the **dedicated-server folder**. A later manager installation does not move this DLL or manage its updates. Follow the pack's [manual instructions](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/#installation-manual) for loader details.

### Shared configuration and maintenance

Startup generates `BepInEx/config/heimkit.cfg` in the active loader location and logs its full path. For updates, stop the server, make a matching backup, replace the DLL in that same active location, preserve configuration and restart through the same launch route.

## Configuration

### Hidden version override

The manual-only `[Compatibility] SupportedValheimVersion = <exact-game-version>` selects one exact requirement for all modules. Use canonical `major.minor.patch`: three nonnegative decimal components, no leading zeros, suffixes, wildcards or lists. Surrounding whitespace is ignored.

Absent values use the audited baseline. Invalid/unreadable values warn once and fall back; a different valid value warns once. Existing supplied values are preserved. The key/section is not generated. Restart applies changes. The [compatibility contract](architecture.md#configuration) defines API/readiness checks; see [limitations](limitations.md#server-observations-and-logs) for the override's meaning.

### Default configuration

Shared sections `[Diagnostics]` and `[PlayerActivity]` precede feature sections. Startup preserves existing values and adds missing defaults. Use [config/heimkit.cfg](../config/heimkit.cfg) as a reference; keep the active file when upgrading.

Feature controls are documented in their guides: [InfiniteTorches](features/infinite-torches.md#configuration), [WeatherGuard](features/weather-guard.md#scope-and-configuration), [AFKNightWatch](features/afk-night-watch.md#configuration), [AFKRaidWatch](features/afk-raid-watch.md#configuration) and [SharedMap](features/shared-map.md#configuration). Stop the server before configuration edits and restart afterward.

### Diagnostics

`[Diagnostics] DebugMode` enables optional diagnostics across features. Each feature and PlayerActivity has its own DebugMode, default false, for selective output. A false feature switch inherits global Debug. Startup adds missing keys even for disabled features.

BepInEx independently filters severity. Add `Debug` to the existing `LogLevels` list under `[Logging.Disk]` for `LogOutput.log` and/or `[Logging.Console]` for console output. Preserve other levels, for example `Fatal, Error, Warning, Message, Info, Debug`, then restart. HeimKit warns when a known listener excludes requested Debug output and identifies the affected setting.

Info covers enablement, readiness, meaningful results and explicit reports. Warnings/errors remain visible with debugging off. A startup Debug declaration identifies the effective global/feature/suppression source. Automatic Debug output pauses after five minutes without connected players and resumes on connection, subject to stream budgets. See [logging](architecture.md#logging) for formats and [log interpretation](limitations.md#server-observations-and-logs).

### AFKNightWatch

Enabled controls sleep. MajoritySleepEnabled and ExcludeAFKFromSleep independently select quorum and idle exclusion. Defaults require strictly more than half after exclusion and a 30-second continuous-quorum grace while anyone remains outside bed. Everyone in bed bypasses the grace. See [sleep settings](features/afk-night-watch.md#configuration).

### AFKRaidWatch

Enabled independently controls automatic raid-candidate protection for idle-only locations. See [raid rules](features/afk-raid-watch.md).

### PlayerActivity

Sleep exclusion and raid protection share one detector. They do not kick players. Health changes are not activity; movement, replicated body rotation, equipment/emotes, station/bed use and authenticated routed actions supply observations.

| Setting in PlayerActivity | Default | Range/purpose |
| --- | --- | --- |
| DebugMode | false | Shared diagnostics; no tracking is started by this setting alone. |
| IdleTimeoutSeconds | 300 | 30–3600 seconds without observed activity. |
| JoinGraceSeconds | 60 | 0–600 seconds of protection from idle classification after join/recovery. |
| MovementThresholdMeters | 0.5 | 0.1–5 metres of accumulated displacement. |
| LookThresholdDegrees | 10 | 1–90 degrees of replicated body rotation. |
| SampleIntervalSeconds | 1 | 0.2–5 seconds between samples. |

Both the idle timeout and join/recovery grace must end before idle classification; their durations are not added. The detector runs during daylight while an enabled consumer needs it. Shared Info AFK notices name the player and current AFK/joined counts. See [architecture](architecture.md#player-activity) for signals/recovery and [AFK limitations](limitations.md#afk-detection-sleep-and-raids).

### Hidden per-feature debug suppression

Add `DisableDebugLogging = true` manually under a feature's existing section or `[PlayerActivity]` to suppress its optional diagnostics. This key is never generated and does not change gameplay, Info notices, warnings, errors or requested reports. The one startup policy declaration remains available to explain suppression.

| Global DebugMode | Feature DebugMode | Hidden suppression | Debug output |
| --- | --- | --- | --- |
| false | false | Missing/false | Off |
| false | true | Missing/false | On for this feature |
| true | Either | Missing/false | On |
| Either | Either | true or invalid | Off |

The rule is `(global OR feature) AND NOT suppression`. Values are case-insensitive true/false; section/key names are case-sensitive. Empty, malformed or unreadable values suppress output and warn once per section/configuration. Manual values are preserved; inline value comments are unsupported. File changes require restart.

The fresh configuration layout does not convert legacy sections or aliases. Unbound entries can remain but do not control current features. To regenerate defaults, stop the server, preserve the active configuration, remove it from the active location and let startup create a new file, then reapply desired values.

### Requested WeatherGuard coverage

Create an empty `heimkit-weatherguard-coverage.request` beside the active `heimkit.cfg`:

```powershell
New-Item -ItemType File -Path 'C:\path\to\active\BepInEx\config\heimkit-weatherguard-coverage.request'
```

HeimKit polls once per second, consumes the file and writes one Info report. Requests run without restart, DebugMode or an enabled WeatherGuard controller. A new file requests another report; requests before a poll coalesce. Ensure the filename ends in `.request`, not `.request.txt`, and the server account can delete it. Invalid access warns once until recovery. This is local filesystem administration, not a new in-game console command. See [coverage fields](features/weather-guard.md#coverage-report).

## Feature lifecycle

Gameplay uses joined-world sessions, established when a character spawns. Empty or unavailable presence releases gameplay controllers/hooks and activity leases; the first joined player resumes enabled features once world services are ready. Additional connections and respawns do not restart them. WeatherGuard releases retained ownership; SharedMap retains accepted stores and performs permitted saves/import separately.

Each enabled module reports Ready once when native services are available. After all modules complete startup/cleanup, PlayerPresence emits one combined active/inactive notice. Character connection/disconnection notices retain names and peer IDs; AFK notices belong to the shared detector. Individual feature processing transitions stay Debug. See [lifecycle contracts](architecture.md#presence-driven-lifecycle).

## Troubleshooting and removal

Read the active `BepInEx/LogOutput.log` and confirm the launch route, configuration path, build identity and compatibility notices. Remove credentials/join codes before sharing logs. Feature guides explain processing fields; [limitations](limitations.md) explains visibility and timing.

To disable a module, stop the server, set its Enabled=false and restart. To remove HeimKit, stop the server and remove its DLL from the active profile/server installation. Torch fuel remains ordinary native data. WeatherGuard releases ownership and adds no persisted protection/creator flag. Preserve SharedMap stores for later use or matching recovery.
