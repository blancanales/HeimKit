# Server setup

Install HeimKit on a Windows Valheim dedicated server. Players use unmodified clients. Each feature's guide is linked under [Default configuration](#default-configuration). Read [safety and limitations](limitations.md) before enabling the mod.

## Supported environment

| Item | Current baseline |
| --- | --- |
| HeimKit release baseline | 0.2.3 |
| Valheim Dedicated Server | 1.0.21; built-in audited baseline. Other versions follow [server upgrade policy](#server-upgrades). |
| BepInExPack Valheim | 5.4.2351 |
| BepInEx runtime | 5.4.23.5 |
| Plugin target framework | netstandard2.1 |
| Build output | `src/HeimKit/bin/<Debug or Release>/netstandard2.1/HeimKit.dll` |

This section is the sole documentation source for HeimKit's supported game, loader, runtime and framework versions. Other documents link here rather than repeating version values. Update this baseline when code guards and dependencies change.

## Live building-restoration settings

[HeimMender](features/heim-mender.md) owns live reload of its section in the existing config file, including enabled/debug controls and a numbered administrative repair request. Save a complete valid section; malformed values retain current settings. Other sections require their documented restart.

## SharedMap data

SharedMap keeps its own data files in addition to the world save. Include them in [world backups](world-backups.md#sharedmap-data); the [SharedMap guide](features/shared-map.md) covers its settings, pin administration and storage.

## Installation

### Before either installation route

1. Stop the server and [back up the world](world-backups.md), SharedMap and ForestRenewal data and existing configuration.
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

### Server upgrades

`[Compatibility] AllowUnverifiedVersions` defaults to `true` and is generated automatically. It lets HeimKit attempt to operate after a server update without entering a new version number. The [supported environment](#supported-environment) still identifies the audited baseline. Feature enable switches and runtime compatibility checks continue to apply independently; a failed feature stops while healthy features continue.

```ini
[Compatibility]
AllowUnverifiedVersions = true
```

A permitted server version newer than the tested baseline requires a completed, verified world checkpoint before HeimKit features activate. HeimKit records that checkpoint for the world/save location and HeimKit/Valheim version pair. Later restarts verify and reuse it, rather than make another backup. The record does not mark the game version as tested. See [automatic upgrade backups](world-backups.md#automatic-upgrade-backups) for timing, location, contents and recovery. A backup failure leaves HeimKit inactive for that startup while vanilla server loading continues.

Set the switch to `false` to require the audited baseline instead. Both modes reject missing, blank, control-character-containing or overlong observed version identifiers; permissive mode does not require the current numeric version format. An invalid or unreadable switch uses strict mode. Restart applies policy edits. Existing installations receive [automatic configuration migration](#configuration-migration). See [compatibility limitations](limitations.md#server-observations-and-logs).

### Configuration migration

Keep the active `heimkit.cfg` when replacing the DLL. On startup, HeimKit removes the obsolete `[Compatibility] SupportedValheimVersion` entry and adds `AllowUnverifiedVersions = true` if the new switch is missing. An existing value for the new switch is retained; an invalid or unreadable value uses the strict fallback described above. Existing feature settings are preserved and missing defaults are added. No manual config replacement is needed.

The known retired diagnostics keys `LogStatus`, `StatusIntervalSeconds`, `LogRefillDetails` and `LogRefillRequests` are also removed from `[Diagnostics]`. Unrecognized entries are otherwise preserved. Supplied hidden `DisableDebugLogging` settings remain intact; absent hidden settings are not generated. Startup logs the full active configuration path and removal of a nonempty version override.

### Persistent feature disables

An exception that disables a feature during startup or later play is saved in `HeimKit/Backups/upgrade-<identity>.failures.xml` under the active BepInEx configuration directory. The record identifies the world/save location, full HeimKit build and Valheim version. Each feature entry includes its first failure's UTC time, operation, exception evidence and the world-upgrade backup paths when a checkpoint was required. Normal warnings, readiness waits and intentionally disabled features do not create entries.

On later starts for that same world and version combination, recorded features remain disabled even with `Enabled = true`. The configuration file stays unchanged. Startup logs name each blocked feature and its record; other features continue. HeimMender's live enable switch also respects this override. Check the release notes for the latest HeimKit release documented as compatible with the running Valheim version.

Installing a different HeimKit version automatically uses a separate failure record: features enabled in configuration are attempted again, and the previous version's disable entries remain as history without blocking the new build. Features configured as disabled stay disabled. A feature that fails again receives a new entry for the new world/version combination. A different Valheim version likewise uses its own record. Replacing the DLL with the same build identity or simply restarting does not clear a disable. When the installed release identifies the running Valheim version as its audited baseline, no unverified-version world checkpoint is required; see [backup behavior](world-backups.md#automatic-upgrade-backups).

For a deliberate retry, stop the server, preserve the failure record, and remove only the affected feature's complete `<Module>...</Module>` element from the file named in the log. Keep the root and world/version fields intact. Removing the whole `.failures.xml` file resets every feature decision for that combination. Restart to apply the reset. Leave the separate backup-completion XML and ZIP intact; resetting a feature does not require another world copy. Changing Enabled alone does not reset a failure.

An unreadable or malformed failure record keeps HeimKit inactive. A failure to save a newly disabled feature emits an immediate error and stops shared processing after feature cleanup. See [log interpretation](limitations.md#server-observations-and-logs).

### Default configuration

Shared sections `[Compatibility]`, `[Diagnostics]` and `[PlayerActivity]` precede feature sections. Startup preserves existing values and adds missing defaults. Use [config/heimkit.cfg](../config/heimkit.cfg) as a reference; keep the active file when upgrading.

StationSupply feeds supported stations from one nearby public chest per intake. Use the [illustrated placement guide](features/station-supply.md#placement-examples) to position chests. In `[StationSupply]`, use `Enabled` to control the feature or the individual station switches to choose which stations receive supplies. Fermenter and Obliterator feeding are opt-in. Restart after changing [StationSupply settings](features/station-supply.md#configuration).

Feature controls are documented in their guides: [InfiniteTorches](features/infinite-torches.md#configuration), [WeatherGuard](features/weather-guard.md#scope-and-configuration), [AFKNightWatch](features/afk-night-watch.md#configuration), [AFKRaidWatch](features/afk-raid-watch.md#configuration), [SharedMap](features/shared-map.md#configuration), [TameTravel](features/tame-travel.md#configuration), [DoorKeeper](features/door-keeper.md#configuration), [ForestRenewal](features/forest-renewal.md#configuration), [SeaSalvage](features/sea-salvage.md#configuration) and [HeimMender](features/heim-mender.md) (live reload). Stop the server before configuration edits and restart afterward.

### Diagnostics

`[Diagnostics] DebugVerbosity = Normal` selects the shared evidence tier. `Verbose` adds detailed topic records when the feature's effective DebugMode permits them; it does not enable debugging or bypass hidden suppression, budgets or the empty-server pause. Restart applies changes. HeimKit prefixes every line with local `HH:mm:ss`; startup also includes the full date. Shared player topics are `presence` and `afk`; see the [logging contract](architecture.md#logging).

`[Diagnostics] DebugMode` enables optional diagnostics across features. Each feature and PlayerActivity has its own DebugMode, default false, for selective output. A false feature switch inherits global Debug. Startup adds missing keys even for disabled features.

BepInEx independently filters severity. Add `Debug` to the existing `LogLevels` list under `[Logging.Disk]` for `LogOutput.log` and/or `[Logging.Console]` for console output. Preserve other levels, for example `Fatal, Error, Warning, Message, Info, Debug`, then restart. HeimKit warns when a known listener excludes requested Debug output and identifies the affected setting.

Info covers enablement, readiness, meaningful results and explicit reports. Warnings/errors remain visible with debugging off. A startup Debug declaration identifies the effective global/feature/suppression source. Automatic Debug output pauses after five minutes without connected players and resumes on connection, subject to stream budgets. See [logging](architecture.md#logging) for formats and [log interpretation](limitations.md#server-observations-and-logs).

### Performance observations

On first occupied activation, required weather protection runs before optional startup work. Other enabled modules begin across updates under the shared budget; ongoing transactions and inactive administrative paths retain ordinary service. InfiniteTorches discovery also advances in small cell batches before sending requests, with current owner/range validation. No settings changes are required.


WeatherGuard Debug timing summaries distinguish completed protection cycles, readiness and scene-list reuse, and independently pending optional probes. `last_cycle_wall_s` includes suspended time. Normal protection, observation and context-readiness stages measure active elapsed work.

HeimKit pauses optional background work entirely while a native random-event raid exists or it observes nearby native combat traffic, including boss targets. This uses native server raid state and the existing server message observer, independently of AFKRaidWatch, and requires no client mod, AFK tracking or extra configuration. WeatherGuard protection and required terrain/support upkeep continue, together with inventory transaction completion, manual access and cleanup. SharedMap sampling, discovery and automatic tables, new supplies, gradual repairs, renewal discovery, salvage recovery and new torch-refill scans wait. Quiet resumption uses ordinary scheduling without replaying missed intervals. Performance windows show combat pressure/boss update counts, raid-paused update counts and combat deferrals, plus native scene pass, binding-maintenance and retention maximums. The current server-wide combat policy is unchanged by these performance repairs. Counts describe observed update windows, not fights won or confirmed damage. See [combat and measurement limits](limitations.md#server-observations-and-logs).

Set `[Diagnostics] DebugMode = true` and `DebugVerbosity = Verbose` for server-wide performance reports. HeimKit can emit one Debug `Performance window` per minute when a player was joined, an update gap reached 250 ms or the native save guard fell back. Shared Debug admission applies, including combat and idle suppression. The window reports sampled plugin-update interval maximum and intervals of at least 250 ms, measured HeimKit update average/maximum, the slowest measured module, and maxima for joined players, WeatherGuard outdoor regions, shared frame-discovery records and scene instances. Native context calls/maximum, log formatting/listener maximum and the slowest listener's feature/topic, ownership attempts, source requests, paired refresh groups, retry/admission deferrals, managed memory and GC collection deltas separate common work sources. Candidate coverage reports the maximum membership of the largest targeted selection, maximum seeded cells, membership-pressure cell trims and selection fallback transitions. The largest update interval retains the preceding measured HeimKit duration and its heaviest module as associated spike evidence. Process CPU-time delta and working set appear when the runtime supports them. These counters need no additional world discovery. See [measurement limits](limitations.md#server-observations-and-logs).

A separate Verbose Debug `Slow work stages` record appears at most once per performance window when measured stages reach 16 ms. It reports up to six stage names with calls, average/maximum elapsed milliseconds and any measured input items, bytes or generation-zero collections from that stage's single slowest sample. SharedMap distinguishes decoding, merging, pin processing, compression, incoming table callbacks and optional pin diagnostics; WeatherGuard distinguishes required protection and scene extension, alongside Core scene retention. Stage scopes can overlap and their maxima must not be summed. Where available, `subject` names the native metadata asset from the same maximum sample; names are bounded to 96 characters. Optional SharedMap asset-preparation records report cache/load outcomes and observed wait seconds, which include admission delays. Stage counters retain no player or object identities and reset at each minute boundary, including when output is suppressed.

Native save-request and synchronous world-save preparation timings also use the slow-stage health record. On the audited game baseline, a ready Windows dedicated server can use a narrow compatibility fallback when native platform metadata is absent; its first use is announced and performance windows count subsequent uses. Authentication, disk checks and actual saving remain native. A request observation is not confirmation of durable save completion.

Optional Debug output stops admitting records after two milliseconds of measured optional formatting/listener work in a plugin update, in addition to its existing per-feature minute ceiling. Normal results, requested reports, warnings, errors and recovery notices remain available. Output resumes on the next update; no extra logging worker or queue is created. The ordinary BepInEx logger and listener settings still control delivery.

With global DebugMode or PlayerActivity.DebugMode enabled, `PlayerActivity.player_context` records correlate named players and character IDs with current location/biome/interior state, damage-time locations and targets, raid proximity, server weather, nearby WeatherGuard observations, sampled NPC target/boss evidence and one example NPC identity and shared-frame workload. They remain available during combat through a small diagnostic budget. Normal verbosity retains all eight context fields. The ceiling is 24 records per minute across players, with at least ten seconds between player samples and a five-minute unchanged heartbeat. Other feature behavior and battle restrictions are unchanged.

The records label player-local weather unverified, damage senders unconfirmed attackers and missing/partial/stale samples explicitly. WeatherGuard native wet-wear gates distinguish rain-wet and underwater causes; zeros do not prove absence elsewhere. `npc_sample` uses existing cached world records within 128 three-dimensional metres and only reads exact target identities for sampled server-owned AI. Its static-target count includes observed building/object targets. Client-owned targets remain unknown. `work` refers to shared frame/module elapsed work, not CPU attributed to that player. The collector adds no world discovery, scene creation, ownership changes or physics probes. PlayerActivity hidden suppression disables it; WeatherGuard hidden suppression excludes its passive contribution. See [observation limits](limitations.md#server-observations-and-logs).

With global DebugMode or PlayerActivity.DebugMode enabled, `PlayerActivity.arrival` adds a short per-player observation window around first character observation, character recovery/replacement and large position jumps. Two-second interim and ten-second final summaries identify the session, origin/destination, observation-gap classification, slowest-update phase breakdown and GC count, overlapping slow-stage samples, shared admission deferrals, destination zone/cache observations and passive WeatherGuard evidence. Partial pending-work evidence covers WeatherGuard protection and SharedMap table reconciliation, with observation ages. Up to twelve arrival records per minute share PlayerActivity's existing output limits, including during combat. Capture starts when a ready peer announces its character, before the character record is necessarily available; unknown destinations are labeled explicitly. In the first two seconds, nearby optional SharedMap pin comparisons are skipped and counted, without queuing them for later. Once the destination is known, distant tables keep their diagnostics. Table validation/publication and gameplay arrival policies remain unchanged. See [measurement and detection limits](limitations.md#server-observations-and-logs).

### PlayerActivity

[AFKNightWatch](features/afk-night-watch.md) sleep exclusion and [AFKRaidWatch](features/afk-raid-watch.md) raid protection share one detector. They do not kick players. Health changes are not activity; movement, replicated body rotation, equipment/emotes, station/bed use and authenticated routed actions supply observations.

| Setting in PlayerActivity | Default | Range/purpose |
| --- | --- | --- |
| DebugMode | false | Shared activity, player performance-context and arrival diagnostics; does not start AFK tracking. |
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

HeimKit polls once per second, consumes the file and writes one Info report. Requests run without restart, DebugMode or an enabled WeatherGuard controller. A [persistent feature disable](#persistent-feature-disables) suppresses WeatherGuard initialization and ticks, including request-file polling. A new file requests another report; requests before a poll coalesce. Ensure the filename ends in `.request`, not `.request.txt`, and the server account can delete it. Invalid access warns once until recovery. This is local filesystem administration, not a new in-game console command. See [coverage fields](features/weather-guard.md#coverage-report).

## Feature lifecycle

Gameplay uses joined-world sessions, established when a character spawns. Empty or unavailable presence releases gameplay controllers/hooks and activity leases; the first joined player resumes enabled features once world services are ready. Additional connections and respawns do not restart them. WeatherGuard releases retained ownership; SharedMap retains accepted stores and performs permitted saves/import separately.

Each enabled module reports Ready once when native services are available. After all modules complete startup/cleanup, PlayerPresence emits one combined active/inactive notice. Character connection/disconnection notices retain names and peer IDs; AFK notices belong to the shared detector. Individual feature processing transitions stay Debug. See [lifecycle contracts](architecture.md#presence-driven-lifecycle).

## Troubleshooting and removal

Read the active `BepInEx/LogOutput.log` and confirm the launch route, configuration path, build identity and compatibility notices. Remove credentials/join codes before sharing logs. Feature guides explain processing fields; [limitations](limitations.md) explains visibility and timing.

To disable HeimMender, save Enabled=false in its live section. For other modules, stop the server, set Enabled=false and restart. To remove HeimKit, stop the server and remove its DLL from the active profile/server installation. Torch fuel remains ordinary native data. WeatherGuard releases ownership and adds no persisted protection/creator flag. Preserve SharedMap and ForestRenewal stores for later use or matching recovery.
