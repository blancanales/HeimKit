# HeimKit architecture

## Scope and current status

HeimKit is one C# BepInEx plugin for Windows dedicated servers. InfiniteTorches, WeatherGuard, AFKNightWatch, AFKRaidWatch, SharedMap, DoorKeeper, ForestRenewal, SeaSalvage and TameTravel are independently configured modules. SharedMap owns terrain/table filtering and optional portal, resource, dungeon, runestone and recurring-spawner markers. See [feature specifications](specs) for behavior and [supported environment](server-setup.md#supported-environment) for versions.

Modules must work with unrelated modules disabled and isolate failures/cleanup. This document owns shared design; [contributing](contributing.md) owns procedures, [server setup](server-setup.md) owns operations, and [limitations](limitations.md) owns user-facing caveats.

## File placement

```text
AGENTS.md, README.md, LICENSE
src/HeimKit/
  Plugin.cs
  Configuration/        # Shared settings, loading and compatibility
  Shared/               # Logging, lifecycle and player services
  Features/<Name>/      # Settings, lifecycle, processing, diagnostics and Models/
config/heimkit.cfg      # Optional reference defaults
docs/
  features/             # User guides
  specs/                # Feature contracts
  architecture.md, contributing.md, server-setup.md, development.md
  limitations.md, world-backups.md
  images/
```

| Responsibility | Location |
| --- | --- |
| BepInEx entry point and module dispatch | `src/HeimKit/Plugin.cs` |
| One module's implementation, settings and helpers | `src/HeimKit/Features/<FeatureName>/` |
| Reusable contracts, log formatting and sample budgets | `src/HeimKit/Shared/` |
| Shared config-file creation, loading and saving | `src/HeimKit/Configuration/` |
| Optional example configuration | `config/heimkit.cfg` |
| Module behavior and acceptance criteria | `docs/specs/<feature-name>.md` |

Use one folder inside Features for each module. Put its class files inside that folder, not directly under Features. Shared helpers require multiple implemented consumers or an application-wide contract. Shared code must not implicitly enable a module or require another module's private state.

## Naming

- Display each feature using only its chosen name, such as `WeatherGuard`. Do not append labels such as `prototype`, `observation prototype`, `experimental`, `beta` or a development-stage suffix to the feature name in startup, registration, log prefixes, configuration sections or documentation titles. This applies during local development as well as publication.
- Put current behavior and limitations in message bodies and feature specs, separately from the feature name. For example, use `WeatherGuard: ENABLED` and a separate `WeatherGuard: Current behavior ...` message. Diagnostic categories belong after the colon, such as `WeatherGuard: evidence; ...`. Helper class names may still describe their responsibility.
- Use plain responsibility names for WeatherGuard helpers: Controller, Patches and Rules. Do not name project-owned helpers Simulation; retain the original names of game APIs.
- Class and interface names do not contain the word `Feature`.
- Use names that describe responsibility, such as `InfiniteTorches`, `InfiniteTorchesPendingRefills`, `ConfigurationLoader` and `IServerModule`.
- Feature-owned behavior classes use `<FeatureName><ResponsibilityOrAction>`; match filenames to their primary type names. Shared classes use descriptive responsibility names without a feature prefix.
- Model types and enums use descriptive names. A feature prefix is guidance, not a requirement; use one when it improves clarity or avoids ambiguity.
- Use `HeimKit.Features.<FeatureName>` for module namespaces, and `HeimKit.Shared` or `HeimKit.Configuration` for shared code.
- The Features folder name remains intentional; the naming restriction applies to class/interface names.
- Use the plugin identifier `heimkit`. The user-facing configuration filename is independently defined as `heimkit.cfg`. Configuration loading uses an explicit path independently of the plugin identifier.

## Module lifecycle and isolation

`Plugin.cs` registers modules, initializes them, dispatches updates to active modules and shuts them down. It should not contain torch selection, refilling or other module-specific gameplay algorithms.

Each feature main class owns settings construction, compatibility, lifecycle, startup state and failure isolation. Its controller owns gameplay processing and reports processing failures to the main class. This boundary applies consistently to InfiniteTorches, WeatherGuard, AFKNightWatch and AFKRaidWatch.

Each module implements `IServerModule`:

| Member | Contract |
| --- | --- |
| `Name` | Human-readable module name for logs. |
| `IsActive` | Whether the module is permitted to perform work. |
| `Initialize(gameVersion)` | Check its own enable setting and compatibility; report effective state. |
| `Tick(now)` | Perform module work only when enabled and compatible; respect scheduling. |
| `Shutdown()` | Stop work and release module resources. Safe to repeat. |

Disabled modules must not scan world objects, send gameplay messages or install active patches. The entry point may tick inactive modules to service explicit local administration. A module must gate gameplay independently; WeatherGuard's requested coverage reports describe inactive/unobserved scope without starting a controller or scanning world objects. Shut down scheduled work and remove any owned patches/subscriptions when those mechanisms are added. Config edits require a server restart except HeimMender's feature-owned, debounced section reload. That observer remains independent of gameplay and cannot change other modules.

A module error must disable and report that module rather than deliberately disabling unrelated modules. The entry point catches lifecycle failures per module. Shared ModuleCleanup reports cleanup errors and continues remaining cleanup steps; feature controllers clear active registrations before disposing them. Cleanup errors must not interrupt other modules. Construction/registration failures are also caught per module. A failure in the shared configuration setup currently prevents startup and must be reported clearly.

## Configuration

The plugin resolves its config directory from `BepInEx.Paths.ConfigPath`; it must not hard-code a drive letter or search the disk for BepInEx.

Startup order:

1. Check for `heimkit.cfg` in the loader-provided directory.
2. Load existing values, or create an in-memory config if absent.
3. Bind all implemented module settings and defaults with automatic intermediate saves disabled.
4. Save the complete configuration, creating the directory and file as needed, then apply ConfigurationLayout so shared sections precede feature sections.
5. Log whether configuration was created or loaded, including its full path.
6. Initialize modules only after configuration setup succeeds.

Existing recognized values must be preserved, and missing settings added with defaults. A load/save error must be reported without claiming successful setup or enabling affected functionality.

`Configuration/ConfigurationLayout` owns the shared-section registry and groups `[Compatibility]` (when manually present), `[Diagnostics]` and `[PlayerActivity]` before all feature sections after the loader saves. BepInEx otherwise sorts sections alphabetically; binding order alone cannot control layout. The same layout is reapplied after its automatic setting-change saves. Preserve complete section blocks, generated descriptions and all bound/unbound values; layout does not rename or migrate settings. Feature sections have no required relative order. Register any new shared configuration section in ConfigurationLayout and place it before feature sections in documentation examples and, for generated settings, the reference config. Manual-only sections remain absent from the reference config.

Each module has its own section and `Enabled` setting. Torch settings currently use `[Torches]`. Keep the optional `config/heimkit.cfg` consistent with code defaults, but never require users to manually create or copy it. The built DLL must generate a complete config on first use without the example file.

`Configuration/GameVersionCompatibility` owns the built-in audited game version and the manual-only `[Compatibility] SupportedValheimVersion` override. All modules resolve the same immutable policy per ConfigFile during construction and compare the observed game version against its exact required version before gameplay activation. Do not substitute the configured value for the observed game version. Absent keys are not bound or generated; existing unbound values are preserved. Invalid/unreadable entries fall back to the built-in requirement with one Warning per loaded configuration. A different valid requirement also warns once, explicitly distinguishing operator authorization from an API/gameplay audit. Restart applies changes. Runtime API/signature, native networking and feature lifecycle checks remain independent. Full operator usage and validation rules belong in [server setup](server-setup.md#hidden-version-override).

## Logging

HeimKit uses the topic-based grammar below. BepInEx adds its own severity/source prefix outside this record. Info and Warning wording is retained from HeimKit except for WeatherGuard's prevention roll-up; Debug and Error describe outcomes in complete sentences.

```text
Info / Warning: HH:mm:ss Feature: Sentence.
Debug / Error:  HH:mm:ss Feature.topic: Sentence. key=value key="value with spaces"
```

Every record starts with server-local `DateTime.Now`, formatted with invariant culture. `DiagnosticLog.WallClock` is injectable. Startup also includes the full local date and time in its version sentence. Each record occupies one physical line; sentence control characters become spaces. Exception details use `error_type`, `error` and escaped `stack` fields, following the incident limits below. The elapsed-time field is absent. Debug policy declarations carry `log_schema=2`.

`Core/Diagnostics/DiagnosticLog` owns formatting and severity preservation. Topics are immutable `LogTopic` values validated against `[a-z][a-z0-9_]*`. Shared topics live in Core; feature-owned topics live in each feature's LogTopics class.

| Shared topic | Meaning |
| --- | --- |
| `lifecycle` | Startup, readiness, enabled state and failures. |
| `config` | Debug policy and settings declarations. |
| `scope` | Current scope and changed processing summaries. |
| `timing` | Bounded processing measurements. |
| `presence` | Shared player connection and population observations. |
| `afk` | Shared activity and idle-inference observations. |

Feature topics and their subjects are listed in the [feature guides](features). A message must state its observed outcome and relevant reason without requiring the reader to interpret fields. Use an available character name in the sentence; supporting fields can retain object and peer identity. State the observation window in prose: this pass, the last minute or controller lifetime. Counter keys do not use snapshot, pass, interval or total suffixes; lifetime counters use a descriptive `lifetime_` prefix.

### Fields and verbosity

Fields use logfmt with exactly one separating space. Keys match `[a-z][a-z0-9_]*` and are unique within a record. Values are unquoted unless empty or containing a space, quote, equals sign, backslash or control character. Quoted values escape backslashes, quotes, newlines and carriage returns; other control characters are escaped as well. Invariant formatting uses lowercase booleans, plain integers, two decimals for fractional milliseconds, one decimal for metre/second measurements and positions, and up to three decimals for other fractional numbers. Vector3 positions use `x,y,z` with one decimal and no spaces. Native object IDs retain their `user:id` text.

Normal fields omit null, empty strings, `unknown`, numeric zero, false and the empty object ID. Dropped-record counts are empty when nothing was dropped, so they appear only when a sample stream overflowed; their values name lowercase sample streams, never the retired categories. `FieldAlways` retains meaningful zero/false values. Null remains omitted rather than invented. Normal lines carry no more than eight fields; defensive overflow keeps the first eight and appends `omitted_fields`. Invalid or unsupported optional fields cannot throw a formatting error that disables gameplay.

`[Diagnostics] DebugVerbosity` selects `Normal` (default) or `Verbose` for HeimKit. It does not enable debugging. Verbose evidence requires the feature's effective debug policy and the Verbose setting together, uses the same subject topic, and may exceed eight fields. Its sentence names the subject (the player, piece, marker or window) so it stays readable when other records interleave. When a Normal or Error record hands its remaining fields to Verbose, Verbose starts at the first field the record did not show. Both tiers share the existing per-feature ceiling of sixty optional Debug records per minute. Build optional field sets only after their policy, cadence, sample and tier gates permit emission. `FeatureDiagnostics` owns the shared Normal/Verbose gates. Formatting reuses a thread-local StringBuilder and concrete collection enumerators, with no LINQ or per-record HashSet; warmed formatting allocates only the resulting string.

### Standard feature events and grouping

Normal output reports configuration, enabled state, one player-independent server Ready, shared active/inactive presence, meaningful observed results and actionable failures. Disabled or unknown scope remains distinct from empty observed scope. Ready does not establish successful gameplay effects. Runtime results and requested administration remain independent of DebugMode.

| Source | Standard output | Keep in Debug |
| --- | --- | --- |
| Every feature | Enabled/Disabled, one server Ready, shared presence status and actionable failures | Configuration details, activation/pause reasons and unchanged readiness checks |
| PlayerActivity | Named AFK entry/exit, counts and tracking-loss reasons | Activity signals and inference evidence |
| InfiniteTorches | One observed fuel-update count per reconciliation pass and grouped unsupported-prefab warnings | Individual refill requests and results |
| AFKNightWatch | Confirmed sleep starts permitted by configured rules | Quorum, exclusions, native timing and gate evidence |
| AFKRaidWatch | One excluded-candidate count per outer native selection pass | Candidate reasons, population and native schedule/setter evidence |
| WeatherGuard | At most one nonempty prevention roll-up per ten-minute window, plus a pending-window flush on controller disposal; capacity state changes and requested coverage reports | Protection, ownership, wear and rain observations |

Group at the producer's logical operation boundary. Empty and unchanged operations emit nothing. Keep first failures, lifecycle transitions, requested reports, distinct failures and named players identifiable. WeatherGuard commits only completed successful native sweeps to its roll-up. The roll-up counts each distinct piece once per window with a bounded native-ID set; the owning [specification](specs/weather-guard.md) describes overflow and flush behavior. Ownership acquisition, roof geometry or unchanged health alone cannot report prevention. See [observation limits](limitations.md#server-observations-and-logs).

### Debug policy startup declaration

Each module emits one `config` declaration when global/feature debugging or hidden suppression is requested, including disabled modules. It records policy source, both switches, suppression, effective configured eligibility, feature enablement, configuration section and `log_schema=2`. All-off configurations are quiet. The suppression declaration is the sole exception to that feature's suppression gate and does not enable recurring evidence. Preserve loader Debug filters and the empty-server grace gate. Settings declarations use `config`; fixed settings are not repeated in recurring summaries.

### Empty-server debug pause

The shared ServerDebugActivity gate permits an initial five-minute startup grace and restarts grace when the last connected peer leaves. Plugin refreshes presence before modules; loading connections count as present. A connection immediately permits Debug again, subject to feature policy and budgets. Unknown population does not start a new empty interval. Monotonic rollback safely resets grace.

`DebugLoggingPolicy.ConfiguredEnabled` combines global OR feature DebugMode with the manual-only hidden DisableDebugLogging override. `Enabled` also applies the shared activity gate. Suppression skips optional snapshots, histories and probes while required ownership, support, inference and gameplay continue. Missing hidden keys are not generated; supplied values are preserved. Invalid values suppress that feature and warn once per section/configuration. See [configuration](server-setup.md#hidden-per-feature-debug-suppression). Verbosity does not acquire activity leases or bypass suppression. DiagnosticLog applies a final gate before formatting.

### Repetition and volume limits

| Output | Emission rule |
| --- | --- |
| Routine Info | Startup or meaningful transitions; no repeated polling status. |
| Debug summary | At most once per minute on semantic change; one compact unchanged heartbeat per five minutes when relevant. |
| Empty or inactive scope | No routine summary or heartbeat; a transition may be recorded once. |
| Debug samples | Preserve each existing stream cap and semantic deduplication, within the overall feature ceiling. |
| Warning incident | First occurrence immediately; at most one concise reminder per five minutes with suppressed repeats. Report meaningful recovery once. |
| Error incident | First occurrence with exception fields immediately; compact reminders at most once per minute. Full stack returns for changed failures or recovery followed by recurrence. |
| Requested report | Respond to every consumed request, independently of automatic budgets and debugging. |

Deduplication uses stable operation, exception type and throw location, plus relevant actor/object identity. Ignore timestamps, advancing counters and variable exception messages. Bound retained identities, clear them at lifecycle boundaries and retain distinct failures. Timer expiry alone is not a changed failure. Optional Debug output has one ceiling of sixty records per feature per minute, including Verbose evidence, summaries and heartbeats. Suppressed log attempts do not suppress gameplay counters or processing.

WeatherGuard retains its existing per-stream bounds: twelve compact protection samples, six object/loading/acquisition samples per stream, six command observations and at most three distinct candidates per protected/exposed wear subgroup per minute. Detailed diagnostics retain their existing level gate; Verbose controls the additional field evidence. InfiniteTorches retains four requests and six outcomes per minute. AFK sleep and raid streams retain their independent six-sample native/candidate limits and existing semantic caches. Summaries identify omitted log attempts; they do not imply lost gameplay measurements.

ProcessingTiming emits at most once per minute for relevant scope: `N passes in the last minute, avg X ms, max Y ms. scope=S`. Measurements cover the synchronous controller operation only; [measurement limitations](limitations.md) remain unchanged.

## Player activity

`Shared/Players/PlayerServices` is owned by Plugin and injected into all feature constructors. Its IPlayerPresence source takes one native peer census per update. ConnectedCount counts transport peers (including incoming/loading connections); ReadyCount counts completed native handshakes, which alone do not establish world entry. JoinedCount and HasJoinedPlayers drive gameplay lifecycle notifications. A native ready peer joins once the server receives a nonempty CharacterID, registered by Game.SpawnPlayer after creating the character. Keep that same peer session joined during death/respawn, when CharacterID can temporarily become empty. Disconnect removes the session; a new peer object must establish its own spawn even if it reuses an ID. World replacement resets joined tracking. Unavailable observations pause consumers without falsely announcing a logout. The Debug startup/empty grace still uses transport presence so loading diagnostics remain visible. Null counts mean unavailable, never zero.

PlayerConnectionNotices emits an immediate PlayerPresence Info entry for each named character connection and disconnection, using the same PlayerPresence census with no AFK lease or Debug requirement. A connection is established at the first ready character spawn, not a transport handshake. Cache the native peer ID and character name by peer object so a departure retains its identity after native fields disappear and separate sessions with the same name or reused peer ID remain distinct. Keep the last nonblank observed name, normalize control characters, bound names to 128 characters and fall back to the peer ID if absent. Preserve session records across unavailable observations and death/respawn; only absence from a successful peer census establishes a disconnection. Clear session records silently on world replacement and shutdown. These per-player notices remain separate from coordinated aggregate feature-status notices.

Refresh live readiness and character/session state each update, but reuse the peer snapshot array while its ordered peer references are unchanged. Connection notices reuse census/departure buffers and sanitize names only when the raw name changes. Routed-message observers refresh their iteration snapshot only on subscription changes; callback removal/failure still prevents later delivery to a removed subscriber and restores the packet cursor for each consumer.

PlayerPresenceEvents coordinates PlayerPresenceDiagnostics with consumer cleanup. Emit `PlayerPresence: No players have joined the world; all features are inactive until a player joins.` once for initial zero joined players and once after the last joined session leaves, only after every registered enabled gameplay consumer acknowledges completed cleanup. Do not emit individual empty-server pauses or repeat the aggregate while empty. Disabled/incompatible modules have no gameplay registration and do not hold up the aggregate. With no enabled consumers, the empty notice still establishes occupancy. A failed empty/unavailable callback blocks the aggregate for that empty interval even after failure isolation removes the subscription; retain error reporting and continue other consumers. A new joined interval resets this coordination. Unknown population is not empty and preserves the last announced occupancy. Emit `PlayerPresence: Players have joined the world; all enabled features are active.` once after all registered enabled consumers confirm successful initialization for the first joined session. Delayed native readiness defers the notice; additional joins do not repeat it. Check activation after callbacks and module ticks. An inactive consumer removed during an occupied interval blocks an activation claim for that interval. Incoming connections, completed handshakes without character spawning, additional joins and respawns do not produce occupied transitions.

`Shared/Players/PlayerProcessingNotices` separates one-time server readiness from player-dependent processing. Every enabled compatible module checks its native dedicated-server service prerequisites on Tick even when empty. Once those services exist, ServerReady emits Ready once for the plugin session; it requires no joined player and does not install gameplay hooks or acquire an activity lease. This is server status monitoring, separate from player-dependent controllers. InfiniteTorches requires ZNet, ZDOMan, ZNetScene and ZRoutedRpc; WeatherGuard also requires ZoneSystem; AFK modules require the shared activity service's native readiness prerequisites. Gameplay controllers retain their existing initialization, compatibility and prefab/hook checks and failure handling.

Register live server-ready, empty-cleanup and running-state getters with Events.Subscribe. The combined presence status waits until every registered enabled module has reported Ready, so empty startup orders Enabled entries, Ready entries, then one inactive presence entry. Call EmptyPaused only after controller disposal returns successfully. Controller cleanup attempts every resource and reports errors, propagating incomplete cleanup to isolated module failure handling instead of acknowledging a pause. Runtime processing activation, empty/world/unavailable pauses and recovery are optional Debug, gated by the owning module's effective policy; no per-feature Paused or Resumed appears at Info. Warnings/errors remain visible. Reset readiness only on explicit module shutdown/reinitialization, not ordinary player departures. Neither occupancy nor construction proves processing is running. Retain presence registration through ordinary pauses; consumer callbacks use state directly, never logs.

`Shared/Players/PlayerActivityService` implements `IPlayerActivityObservations`. `Configuration/PlayerActivitySettings` binds the five restart-required detection settings and a visible DebugMode defaulting false under `[PlayerActivity]`. Its shared DebugLoggingPolicy resolves the manual-only DisableDebugLogging override in the same section. Models live under `Shared/Players/Models`; tracker, routed reader, transition samples and observation counters use responsibility-based shared names. Sleep policy and evidence stay in AFKNightWatch; raid policy and evidence stay in AFKRaidWatch.

The service samples ready peers' valid, network-owned characters while retaining loading peers as unknown observations. Known does not mean active: distinguish Known/Alive/InBed from inferred Idle and consult Reliable after Snapshot. Unknown/nonfinite/loading records and known dead actors are never inferred idle. Preserve native eligibility on unavailable service or unreliable inference; sleep also requires a known living bed sleeper. Movement is cumulative from the activity anchor; replicated body rotation, equipment and emote changes count, and bed/station occupancy holds players active. Health changes never count as activity, including food-related decline, regeneration and incoming damage. Visibility limits are defined in [AFK limitations](limitations.md#afk-detection-sleep-and-raids).

Health is read only to establish finite actor data and living/dead eligibility. No health increase or decrease resets the activity timer or exits AFK. Native Player.UpdateFood can lower maximum health and clamp current health without damage; incoming damage also does not demonstrate player input. Maximum health and damage cause are therefore unnecessary for activity classification. Death, loading and unreliable observations retain their existing exclusion and recovery rules. Existing authenticated routed activity remains independent: an observed outgoing RPC_Damage counts for its authenticated sender, not the target. No health, ownership, damage handling or network traffic is modified, and no client changes are required.

The native replicated crafting animation holds station users active. Authenticated routed chat, animation triggers and native damage/repair/removal/container actions reset activity where observed; housekeeping does not. The observer associates the physical RPC connection with a ready peer and owned character, checks the serialized sender, and restores the packet cursor without modifying bytes, recipients, parameters or native execution. It installs no custom client handlers. A native container-open request holds a ten-second pending grace; an observed container owned by that peer with native InUse remains active until released. It stores at most one target per connected peer and does not load containers, change ownership or read inventories.

Unknown/nonfinite actor data, death, character changes, reconnects, wall-clock rollback and observation gaps longer than three sample intervals plus one second reset inference and grace. Bed occupants remain active actual sleepers. Sampling uses unscaled wall-clock time, so night skipping does not advance the idle clock. Connected actor capacity is 4,096; overflow inserts uncertainty, emits a bounded ordinary warning and retains native decisions. Observer failures clear inference and use the shared warning-incident limits; native packets still execute.

### Ordinary AFK notices

`PlayerActivityService` owns one `PlayerActivityNotices` stream shared by all activity consumers. On a confirmed transition it emits an immediate Info notice naming the character and native peer ID, whether the player became AFK or left AFK status, and the current inferred-AFK count out of joined players. The count describes the latest detector snapshot, including all confirmed idle actors; the joined denominator includes respawning joined sessions but excludes connections that have never spawned. Names come from the shared native peer snapshot; absent names fall back to peer ID. Names are bounded and control characters normalized to keep notices on one line.

These notices require an active inference consumer, but never require DebugMode. Feature debug switches, hidden debug suppression, automatic Debug pauses and optional sample budgets cannot suppress them. Initial active players, unchanged states, repeated Snapshot calls and additional consumers emit no extra notices. The sampling interval still governs replicated changes; authenticated routed activity can announce an exit as soon as the detector observes it. AFKNightWatch and AFKRaidWatch must not log duplicate AFK transitions. Optional PlayerActivity Debug evidence remains independently bounded.

Departure, death, missing actor data, session changes and inference resets clear classification with an explicit reason; none imply observed human activity. A tracking reset announces exits only for previously AFK actors, clears the notice state and reports the AFK count as unavailable while observations are unreliable. An observed empty server reports zero joined and zero AFK. Recovery restarts inference grace and does not fabricate active/AFK events. Shutdown or the last lease release clears notices; releasing one of several leases preserves the shared state. Logging failures are isolated from gameplay inference.

### Presence-driven lifecycle

PlayerServices publishes its refreshed occupancy through `Events.Subscribe(changed, failed, emptyPaused, active, serverReady)` on the main thread, before activity sampling and module ticks. The callback receives true for occupied (after native character spawning), false for no joined players and null for unavailable presence. Subscription immediately replays current state. Only a change of these states triggers another callback; additional logins or departures while others remain do not restart consumers. Notifications use the same census as the Info occupancy notices, rather than reading or parsing logs. A failed callback is unsubscribed and its module failure handler invoked without blocking other subscribers; a failed failure handler is reported separately. Dispose registrations on shutdown and initialization rollback.

Enabled AFKNightWatch and AFKRaidWatch subscribe once and retain the subscription while empty. An empty or unavailable event disposes their controllers, gameplay/diagnostic hooks and activity leases without changing configuration Enabled. Occupancy permits initialization once world readiness exists; Tick also checks world readiness so connection before world startup is safe. The last activity lease removes its routed-message subscription; the shared native observer remains only while another consumer needs it. First reconnect recreates resources and fresh grace; disabled or failed modules do not restart automatically.

InfiniteTorches subscribes to presence but does not acquire an AFK lease. Its refill work requires ready connected owners and scans their active sectors. Empty/unavailable presence releases its controller and pending-request state; occupancy recreates it, with normal world/prefab checks retained. It uses the same peer snapshot for selecting ready owners. It sends no offline refill requests.

WeatherGuard subscribes to presence without an activity lease. Empty/unavailable presence disposes its controller, removes its gameplay hooks, releases protected pieces to vanilla ownership and clears retained regions. Occupancy recreates protection once native world services are ready. Explicit coverage requests remain serviced independently, including while paused or disabled.

Every feature must use this lifecycle for its ordinary processing unless its specification identifies a concrete responsibility in one of these exception categories:

- Additional backup or save management.
- Server health or status monitoring.
- Explicit administration or access management.
- Intentional offline world processing.

A server-side implementation or timer alone does not qualify for an exception. Describe the independent trigger and why the responsibility must run without players. Isolate that responsibility from player-dependent controllers; an operator report does not exempt the feature's gameplay work. Configuration enablement, world readiness, occupied state and activity reliability remain separate conditions. Empty/unavailable presence pauses controllers while retaining the registration, rather than calling module Shutdown and losing the reconnect notification.

### Required feature hookup

Use this procedure for new features and lifecycle changes, alongside the logging contract:

1. In the feature spec, add a `Player-service integration` section naming the processing trigger, the module/controller that owns each resource, the native readiness requirements, whether idle inference is needed, and any independent empty-server responsibility with its exception category and trigger. Link to this contract.
2. Inject the existing Plugin-owned `PlayerServices` into the module constructor in `Plugin.cs`. Store it; never create another hub or infer population with a separate peer census.
3. After enablement and compatibility checks, mark the module active and subscribe once with `_players.Events.Subscribe(PresenceChanged, Fail)`. Retain the returned `IDisposable`. Subscribe immediately replays true/false/null, so the callback must work before assignment of the returned registration and during initialization rollback.
4. In `PresenceChanged(bool? occupied)`, record the state. On false or null, clear the controller reference before disposing it. Disposal removes gameplay hooks, pending work, retained ownership and activity leases. On false, acknowledge the empty pause only after disposal succeeds; provide server-ready, empty-cleanup and running-state getters to Events.Subscribe. Cleanup failures must propagate after all release attempts and cannot acknowledge success. Keep configuration Enabled and the presence registration unchanged. On true, record occupancy; module Tick reports server readiness before starting at most one controller after native readiness checks. Only joined-world sessions count as occupied; actor-dependent work still requires usable ready characters.
5. In Tick, enforce enablement, occupancy and native readiness again. Retry initialization when a connection precedes world readiness; dispose stale controllers on readiness loss or world replacement. Repeated occupied notifications or additional players must not recreate resources. Run explicitly exempt responsibilities outside the gameplay gate.
6. Route callback failure to the owning module's failure handler. Shutdown/rollback/failure marks it inactive, clears the stored registration and controller references, and disposes both with isolated cleanup. A failed module stays inactive until explicitly initialized again. Never dispose another consumer's resources or the shared hub.
7. Acquire shared activity only when the controller actually needs idle inference, following [Integrating a consumer](#integrating-a-consumer). Presence subscription alone does not start AFK observation. Keep logging independent: the shared service owns empty/first-player notices, and subscriptions consume state directly.

Current hookups:

| Module | Presence-controlled work | Activity lease | Independent empty-server work |
| --- | --- | --- | --- |
| AFKNightWatch | Sleep controller and vote hooks | Only with ExcludeAFKFromSleep | None |
| AFKRaidWatch | Automatic raid candidate hooks | While its ready controller runs | None |
| InfiniteTorches | Refill controller and pending requests | None | None |
| WeatherGuard | Protection controller, gameplay hooks and retained ownership | None | Explicit coverage requests (administration/status) |
| DoorKeeper | Door/context observations, native instances and ownership fencing | None | None |
| ForestRenewal | Standing-stump/destruction observation, durable renewal decisions, structure footprints and fenced stump-to-sapling replacement | None | None |
| SeaSalvage | Source settling, terrain/water context, item transfer and short source ownership fences | None | None |
| TameTravel | Authenticated follow commands, portal traversal observations, destination context and original-animal ownership transactions | None | None |
| SharedMap | Position sampling, portal/resource/dungeon/runestone/spawner discovery, depletion observation, grouping/publication selection, table initialization and map-field filtering; world map and publication state retained across pauses | None | Dirty terrain/discovery/publication-state saves and lifecycle flushes (save management); absent-file first-run read-only table import (intentional offline world processing) |

### Integrating a consumer

1. Accept the Plugin-owned PlayerServices in the feature constructor and pass it to the controller. For player-dependent work, subscribe once through Events.Subscribe, release controllers on false/null, and retain the subscription for the next true event. Read `Presence.HasJoinedPlayers`, JoinedCount, ConnectedCount and ReadyCount without taking another peer census. Unknown presence is not an empty server; retain native decisions whenever required state is unavailable.
2. Acquire `players.Activity.Acquire(() => diagnostics.Enabled)` once per initialized controller only while an occupied, ready controller needs idle inference. AFKNightWatch uses `settings.RequiresActivity` (Enabled and ExcludeAFKFromSleep); majority sleep alone reads the character roster from the same presence snapshot without a lease. AFKRaidWatch always leases activity when initialized and enabled. Do not acquire on every tick or native hook.
3. Retain the returned Lease and use its Service.Snapshot(now) for both decisions. Plugin ticks activity throughout daylight whenever any lease exists; the interval cache prevents a native hook or second consumer from resampling the same interval. With no connected players or unavailable world/presence, the service clears inference and skips player sampling. Empty sleep decisions and HeimKit raid filtering are bypassed; native schedulers continue.
4. Check Service.Reliable after reading. Shared observer/sampling failures clear stale views and inference, retain registrations and report bounded warnings/structured errors independently of debug. Recovery starts fresh grace. Features must retain native gameplay when unavailable and may isolate their own failure without shutting down the hub or another consumer.
5. Dispose the lease on empty/unavailable presence, initialization rollback, world-readiness loss, failure and shutdown. Dispose the presence registration on rollback/failure/shutdown; keep it during an ordinary empty-server pause. Disposal is idempotent. The first activity lease subscribes to PlayerServices.RoutedMessages; the last releases that subscription and clears actor/container/diagnostic state. PlayerRoutedMessages owns one native routed observer across independent consumers and removes it after the last subscription. SharedMap resource discovery can subscribe without acquiring AFK inference; stopping either consumer preserves the other. Each callback restores the packet cursor and failures release only the failed subscription. Plugin disposes remaining leases after shutting down modules. World-service changes and reconnects discard old inference; repeatable cleanup must continue after an individual resource failure.
6. Keep gameplay independent of the debug callback. It controls only optional shared diagnostic collection and output. PlayerActivity has one shared transition stream (at most one changed-observation summary and six actor details per minute, with bounded pending/replacement counts), enabled by global debug, PlayerActivity.DebugMode or any currently registered consumer's effective debug policy. PlayerActivity.DisableDebugLogging has final precedence over the entire shared stream; consumer feature suppression still applies to that consumer's contribution. The shared five-minute cutoff and budgets still apply. PlayerActivity.DebugMode enables no feature-specific output and does not acquire a lease; without a gameplay consumer no tracking starts. PlayerServices reports the shared policy once on its first Tick after configuration setup. Enabling raid debug continues to permit shared activity evidence without enabling sleep diagnostics, and vice versa. Do not print shared actor transitions again in feature diagnostics.

AFKNightWatch owns one sleep-vote registration, with a quorum controller only during relevant native afternoon/night sleep attempts. A living sleeper is required before custom evaluation. Native bed checks, cooldown, time checks and broadcasts remain. AFKRaidWatch owns scoped normal automatic candidate hooks and evaluates only native supplied locations. Existing/explicit/forced/standalone events remain native. Their targeted diagnostic contracts are defined in [AFKNightWatch](specs/afk-night-watch.md) and [AFKRaidWatch](specs/afk-raid-watch.md).

## Server-only compatibility

Shared world state and network ownership are different concerns. Inspect both client and server behavior before declaring a module server-only. Server-synced settings alone do not establish support for unmodified clients.

Use vanilla-compatible mechanisms and verify ownership transitions. Review changed APIs and update the relevant spec before changing the built-in audited version. Operators can explicitly replace the exact version requirement through the [hidden version override](server-setup.md#hidden-version-override); this does not change the documented audited baseline or bypass runtime compatibility checks.

## Native world operations

DoorKeeper and SeaSalvage share ServerObjectAuthority and NativeZdoPackets for explicitly retained native-record ownership, packet provenance, stale-data/destruction fencing and release. DoorKeeper permits the native door/damage/repair/removal commands; unpublished salvage sources permit none. Consumers release their own records; native assignments resume after release. Unreadable guarded packets are rejected with a bounded shared Error incident rather than disabling independent consumers; an exhausted object revision fails only its owning consumer. The native scene includes retained door instances while owned. Existing WeatherGuard authority remains independent.

PlayerWorldScope reads the Plugin-owned presence roster, rejects unavailable actors and rotates one actor's bounded sector scope per pass. Its player-clearance checks use current positions and prior movement segments. ForestRenewal and SeaSalvage share NativeWorldObject for ordinary persistent native ZDO creation. WorldTerrainContext retains native terrain/compiler instances and supports DoorKeeper's surrounding-world context; consumers release these inclusions on pause/failure/world replacement. Native scene removal handles released instances without deleting their persistent world records.

PlayerWorldScope preserves its additional sector ring for terrain, plant and structure safety checks around candidates near the action-radius boundary. Consumers retain that sector context and apply their own action-distance filters. Independent scopes preserve each feature's rotating actor and observation history. Shared authority packet hooks bypass peer lookup and parsing while no current-world consumer retains records. Native scene hooks reuse inclusion sets; terrain expiry remains checked at most once per second even without new preparation, using reusable removal lists. Terrain prefab classification is cached per context, and zone preparation uses a cached native delegate. DoorKeeper prepares one due, clear door's neighbourhood per pass and releases ownership after the synchronous close attempt. Packet peer lookups inspect the current census with indexed loops, preserving physical connection identity without a separate stale-peer cache.

Ordinary result grouping for the new modules is one confirmed closed-door, placed-sapling or transferred-item-drop count per pass. Optional summaries report only changed current scope and pass/lifetime results, at most once per 60 seconds. ForestRenewal's durable consumed decisions are save management triggered by occupied gameplay: one immutable ledger snapshot writes in the background, and main-thread world mutation waits for its acknowledged generation; it performs no offline renewals.

TameTravel also owns bounded, explicitly requested follower spacing, with original-record draining before server simulation, ready scene context and release for native riding or portal reservation. Its follow hook changes only retained animals' follow movement; native combat remains. TameTravel owns its traversal/animal transaction fence because source-state draining and destination-owner reconciliation differ from retained static objects. It reuses NativeZdoPackets for native payload boundaries and WorldTerrainContext for destination scene preparation. Authenticated player-data application observes fresh vanilla emotes; PlayerRoutedMessages supplies shared routed input and native ping echoes. Follow/stay results remain per named player, and travel counts group per journey, distinguishing relocation from confirmed resumed ownership. Debug evidence has a separate bounded feature budget.

## Contributor procedures

TameTravel retains a bounded queue of successfully applied owner-position updates alongside authenticated emote observation. Native network callbacks capture evidence only; the feature controller performs source/destination validation, world queries and animal transactions. Traversal diagnostics have their own bounded stream so follow-motion samples cannot suppress entry and cancellation evidence.

Follow [contributing.md](contributing.md) to add or change a feature. Each feature owns a settings class, a main lifecycle class and a Models folder when data types are needed. Helpers follow actual responsibilities rather than a fixed template. Shared ConfigurationLoader handles the single config file; DiagnosticsSettings binds the common debug switch. Feature constructors bind settings before the loader saves and Plugin initializes modules.

Build commands and development references are maintained in [development](development.md). Supported versions and installation belong in [server setup](server-setup.md).

## Native character and terrain readiness

Shared world processing uses NativeCharacterHealth for native living/dead eligibility: an omitted saved health field is the native full-health representation, while explicit nonfinite/nonpositive health remains invalid. This helper never fills in or changes health. WorldTerrainContext verifies native compiler initialization and heightmap/paint readiness before loading saved terrain; only the verified native early-return initialization path is resumed. Incomplete context postpones dependent gameplay, while unexpected failures retain module isolation.

## Documentation content

The root `CHANGELOG.md` owns concise user-facing release notes for Thunderstore, newest release first. It is required in every package and maintained by the [release-note procedure](contributing.md#thunderstore-release-notes). Release headings identify historical package versions; current supported environment values remain in server setup. This release history is permitted separately from prohibited development journals and contains no testing instructions or results.

The root README introduces HeimKit's value for users, describes each feature's benefits and high-level expectations, summarizes compatibility and essential limitations, and links to usage guides and specifications under docs. It also contains a documentation index and license information. Detailed operational and technical content stays in its owning document. The canonical content-placement rules are in [contributing](contributing.md#update-documentation).

The [Supported environment section of server setup](server-setup.md#supported-environment) is the sole documentation source for supported version values; all other documents link there, including the README and development references. Server setup also owns shared operational instructions. Development owns build commands and external development references. Each feature guide owns usage/settings/diagnostic fields; each specification owns implementation contracts. User-facing safety and gameplay/client/log caveats belong in [limitations](limitations.md). Cross-link these documents rather than copying their detailed content.

Keep one current guide and one current specification per feature. Do not append development journals, abandoned designs, per-release histories, task tracking, completion checkboxes or passed/pending test inventories. Testing instructions, results and validation-status notes belong only in the active chat; follow the [chat-only rule](contributing.md#testing-information-belongs-in-chat-only). Keep architecture and agent files focused on durable project rules. Git provides source history.

WeatherGuard retains the shared DebugLoggingPolicy through its controller and diagnostics, rather than copying an initial boolean. Each diagnostic opportunity reads the current loaded global-or-feature setting and suppression/activity gates. This does not add automatic configuration-file reload; file edits still require the documented restart.

DebugLoggingDelivery inspects known native listeners during startup and explains excluded Debug severity through Warning/Info. It preserves listener settings; the operator procedure is in [server configuration](server-setup.md#diagnostics).

HeimMender owns native repair selection, bounded batches and confirmation, terminal administration and its section-only live configuration observer under its feature folder. Only operator-request/configuration handling remains active without presence; repairs always require joined players. Normal results group confirmed repairs by completed automatic or administrative batch; owner, health, queue and deferral evidence remains in Debug.

World-feature Diagnostics own optional shared ProcessingTiming scopes. Effective debug suppression prevents their allocation. They report nonempty controller scope and interval average/maximum milliseconds at most once per minute, excluding native work scheduled outside the controller pass.

Shared ServerObjectAuthority resolves its complete native patch set using exact parameter signatures before installation. The active-area hook targets the instance Vector3 overload; static overloads are distinct native APIs. Constructor rollback removes partial hooks.

## Record-driven candidate discovery

Occupied controllers share one leased set of native ZDO postfixes for successful deserialization, prefab assignment, position, rotation, ownership, typed field writes and removals. Callbacks enqueue bounded, deduplicated identities only. Classification runs in controllers; confirmed destruction enqueues an identity before native record release. The final consumer removes the hooks. Candidate indexes retain IDs in spatial sector cells and resolve current records on use, including identity checks for pooled ZDOs. New player areas receive native discovery; overflow or exhausted area capacity invalidates coverage and re-enters discovery, with broad fallback while candidate capacity is exceeded. World replacement and controller disposal discard coverage. PlayerWorldScope separates actor/clearance sampling, indexed candidate discovery and lazily queried full safety context. Action context remains fresh for each processing pass and rejects oversized scopes.

## Core layer and anti-duplication

The HeimKit performance rebuild lives temporarily under `src/HeimKit2/`; legacy `src/HeimKit/` remains the behavioral reference until cutover. Both implementations retain the HeimKit product name, `HeimKit.dll`, `HeimKit` namespaces and `heimkit` loader/patch identities; deploy only one implementation. Directory renaming does not change application identity. The [performance rebuild specification](ai/heimkit-rebuild-spec.md) defines its placement and shared ownership. Existing lifecycle, configuration, compatibility, logging and player-service contracts apply to both implementations.

Core owns shared scheduling/isolation, world discovery, prefab metadata, native member delegates, ownership/packets, players, diagnostics and cleanup. Feature controllers consume subscriptions and filtered registry catalogs; the catalogs retain selection membership while metadata lives on the registry's single prefab record. World discovery composes native cell results across requested near/far/classic geometry, sharing overlapping cells and a single record-change index. Core owns actor sampling and per-consumer movement/rotation views. Feature rules retain eligibility policy.

| A feature needs to… | It uses… | It must NOT… |
| --- | --- | --- |
| Find world objects near players | `Core/World/WorldObjectProvider` via a `CandidateSubscription` | call `ZDOMan.FindSectorObjects`, build a `WorldCandidateIndex`, or keep its own sector lists |
| Know player positions / proximity / movement | `WorldObjectProvider.Scope` (`PlayerWorldScope`) | re-sample peers or re-derive movement segments |
| Classify a prefab (components, category, health, colliders) | `Core/Prefabs/PrefabRegistry` | call `GetComponent`/`GetComponentsInChildren` on a prefab, or keep a prefab→metadata dictionary |
| Read/write a native private field or call a native private method | `Core/Native/NativeAccess` (FieldRef/delegate) | declare `FieldInfo`/`MethodInfo` fields or call `GetValue`/`Invoke` in hot paths |
| Retain/release ZDO ownership, parse/fence packets | `Core/Ownership/ObjectAuthority` + `ZdoPackets` | re-implement packet parsing, ownership transfer, or destruction fencing |
| Prepare terrain / create persistent world objects | `Core/World/TerrainContext` + `NativeWorldObject` | re-resolve terrain compiler reflection or re-implement zone creation |
| Check living/dead/finite actor health | `Core/Native/NativeCharacterHealth` | read health fields directly for eligibility |
| Know who is connected / joined / AFK; get presence events | `Core/Players/PlayerServices` (injected) | take a peer census, parse logs, or infer population |
| Observe authenticated routed RPCs (chat, emote, damage, container) | `Core/Players/PlayerRoutedMessages` + `RoutedActivityReader` | install its own routed observer |
| Schedule periodic work | `Core/Scheduling/ModuleScheduler` (declared interval + phase) | store its own `_next` float and compare `now` |
| Gate on world-service readiness | shared readiness from `WorldObjectProvider`/`WorldServices` | re-null-check `ZNet`/`ZNetScene`/`ZDOMan`/`ZRoutedRpc` in its own Tick |
| Emit logs / summaries / debug evidence | `Core/Diagnostics/FeatureDiagnostics` base + `DiagnosticLog` | invent its own log formatting or budgeting |
| Load/define settings | own `Settings` class bound through `Core`/`Configuration/ConfigurationLoader` | create a second config file or its own loader |
| Clean up on failure/shutdown | `Core/Lifecycle/ModuleCleanup` | swallow cleanup errors or skip remaining steps |
| Drain and hand off an existing native object with ordered replication | `Core/Ownership/ObjectTransferAuthority`, `OwnershipLease` and `ZdoReplication` | duplicate source-drain, owner fencing or native peer bookkeeping |
| Compare native object identities without allocating strings | `Core/Native/NativeIdentity` | create object-ID strings inside repeated sorting/comparison loops |


New features must consume these services. Do not create a second scanner, prefab metadata cache, reflection cache, packet/ownership manager, presence hub or config loader in a feature folder. Add a shared capability under Core and extend this table when needed. The plugin owns the shared service lifetime; each controller releases only its own views and resources. Configuration remains under `Configuration/`, with the same existing keys, defaults, config filename and persistent data paths.

The shared change feed classifies objects into per-subscription ID sets at discovery and on changes; normal targeted reads visit only matching IDs. Each selection retains at most 131,072 matches, then uses shared broad views without invalidating other selections.

While targeted subscriptions exist, broad native cell snapshots and composed near/far views are shared within a frame. Broad views preserve native centre-outward ring order, native sector deduplication and near-before-distant delivery; targeted reads retain row ordering. With only broad consumers, requests use fresh native scans without the candidate index, change hooks or frame caches. The last targeted subscription releases the index and its hooks. Same-cell changes re-evaluate eligibility without replacing retained membership sets or reordering cached native records.

Seeded coverage and frame caches admit at least 8,192 cells, scale to 512 cells per joined player up to 32,768, and evict the least-recently-used individual entries at capacity rather than clearing all discovery or disabling a module. Reads refresh recency without allocating; removed cache nodes are reused. Record-change queue overflow invalidates coverage for rediscovery. The shared prefab registry admits 65,536 entries, with existing feature catalog limits preserved.

Actor sampling runs lazily at most once per frame after presence refresh; consumer movement segments retain their own observation cadence. Inactive module ticks remain available for explicit administration after gameplay shutdown.

The host reports changed failure identities (operation, exception types and stable throw location) immediately, suppresses volatile-message repeats, and permits a compact repeat-count reminder once per minute. Incident state is bounded to one current failure per module and resets on reinitialization or active recovery.

Consumer-specific native capabilities resolve in that consumer's compatibility check: live scene-instance access resolves only for WeatherGuard, so its failure cannot prevent unrelated modules from initializing. Live components use direct component access; PrefabRegistry is reserved for prefab definitions.

Native identity ordering preserves culture-sensitive tame selection and ordinal portal ordering. Nearby candidate views are read-only, borrowed until the next query, and must be consumed immediately.

Existing per-feature candidate, action and geometry limits still apply. Ownership leases retain feature policy separately from native packet handling. Player activity reuses sampling buffers and separate retained anchor/previous observations, preserving movement, equipment and grace decisions when input objects are reused.
