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

Info and Warning output uses a concise, human-readable format:

```text
Feature: Message
```

Use one short sentence stating the status or problem. A warning includes its practical impact and an action when one is available. Do not append event categories, elapsed time, settings dumps, hook lists, schema metadata or metric fields. Essential information such as an unsupported version or a configuration filename may be included naturally in the sentence. Routine startup reports the application name/version, configuration loaded or created and each feature's configured Enabled/Disabled state. Each enabled feature subsequently reports `Feature: Ready.` once when its required dedicated-server services become available, regardless of players. Ready means the module is prepared to start player-dependent processing; it does not claim installed gameplay hooks or verified effects. Later processing activation and pauses belong in optional Debug output; normal active/inactive status belongs only to PlayerPresence. Normal startup waiting belongs at Debug; there is no overall plugin Ready message. Emit Ready once per module initialization session; player departures and later processing starts do not repeat it. Long behavior explanations belong in the feature guides. Configuration values, debug-policy resolution, hook registration and processing details belong in optional Debug output.

Debug, Error and exception details use the structured format:

```text
Feature: EVENT | Plain-language result | elapsed_s=value | field=value
```

BepInEx supplies the Debug, Info, Warning or Error prefix. Optional diagnostics use Debug; ordinary state output and explicitly requested admin reports use Info. Choose severity from the event's impact, not its formatter: Warning describes a recoverable problem or degraded behavior; Error describes a failed operation or disabled feature. An exception is not automatically fatal. A recoverable exception may have a concise Warning notice, but its exception type, message, stack and supporting fields use a structured Error record, subject to the incident limits below. Do not emit duplicate exception details from multiple layers for the same failure. DETAIL is an event category, not a separate configuration switch or BepInEx severity. Application version is visible at Info startup; `log_schema=1` belongs in Debug metadata. `elapsed_s` is monotonic wall-clock time since plugin startup; native game-clock values use separate fields such as `game_time_s` and observed wet duration.

| Event | Meaning |
| --- | --- |
| LIFECYCLE | Startup, enabled/disabled state, readiness, warnings and failures. |
| SUMMARY | Current scope and activity over a reporting interval. |
| OBJECT | First observation, eligibility change or end of observation. |
| ACTION | An operation performed or request sent; delivery is not implied. |
| RESULT | An observed outcome, its measurements and any evaluation blockers. |
| DETAIL | Supporting timing, loading, calculation or network evidence. |

`Shared/DiagnosticLog` owns both formats and severity preservation. Structured field names contain letters, digits or underscores; project fields use snake_case. Values use invariant numeric formatting without rounding away measured precision, lowercase booleans and `unknown` for unavailable values. Structured backslashes, pipes, carriage returns and newlines are escaped as `\\`, `\|`, `\r` and `\n`, so each message occupies one physical line and delimiters in values are distinguishable. Simple messages retain readable paths and normalize embedded line breaks to spaces. Never treat unavailable client calculations as zero or infer damage cause from health change alone. Counter suffixes distinguish `_now` snapshots, `_pass` latest-pass counts, `_interval` reporting-window events and `_total` controller-lifetime totals.

Startup, readiness, warnings and errors remain visible with `[Diagnostics] DebugMode = false`. Optional troubleshooting requires global OR feature DebugMode and an unsuppressed feature policy; WeatherGuard level and world-item inclusion select diagnostic detail and scope without independently enabling diagnostics. Feature Diagnostics classes own message selection, evidence formatting and debug gating. `Shared/DiagnosticBudget` supplies reusable 60-second sample windows and omitted-message counts. `Configuration/DebugLoggingResolver` resolves the manual-only `DisableDebugLogging` key without binding absent defaults, and `DebugLoggingPolicy` computes (global DebugMode OR feature DebugMode) AND NOT feature suppression. All current modules use this policy. The key is omitted from generated and reference config; BepInEx preserves manually supplied unbound entries on save. Overrides are cached per loaded ConfigFile and feature section. Invalid/unreadable values suppress debug collection/output and produce one standard warning per section/configuration. The [configuration contract](server-setup.md#hidden-per-feature-debug-suppression) defines inheritance, preservation and restart behavior. Future features must use this resolver.

Controllers collect operational measurements and counters independently of printing limits. Feature suppression also skips optional diagnostic snapshots, roof probes and histories; required ownership, support context and refill tracking continue. A suppressed summary poll is not counted as an omitted event. Standard warnings and errors are independent of DebugMode and debug sampling; repeated incidents use the separate limits below.

### Standard feature events and grouping

Ordinary output answers whether a feature is enabled, ready, paused or resumed; what meaningful result it observed; or what problem needs attention. Use concise readable counts, an honest outcome and a useful action where available. Do not promote polling snapshots, ownership bookkeeping, eligibility traces, unchanged votes or heartbeat counters into Info. Runtime results remain independent of DebugMode and hidden debug suppression.

| Source | Standard output | Keep in Debug |
| --- | --- | --- |
| Every feature | Enabled/Disabled; one player-independent server Ready; shared active/inactive presence status; actionable warnings and failures | Configuration/hook details, per-feature processing activation/pause reasons and unchanged readiness checks |
| PlayerActivity | Named AFK entry/exit with current counts and tracking-loss reasons | Activity signals and observation evidence |
| InfiniteTorches | One observed-fuel-update count per reconciliation pass; one grouped unsupported-prefab warning per lookup | Individual requests/results, object IDs, positions and skip counters |
| AFKNightWatch | One confirmed sleep start permitted by configured rules while some players were not in bed | Configured threshold and grace delay, observed/required sleeper counts, exclusions, repeated votes and native start/end timing gates |
| AFKRaidWatch | One count of excluded automatic raid candidates per outer native selection pass | Individual candidate reasons/locations, retained candidates, chance/schedule checks and native setter traces |
| WeatherGuard | One confirmed weather-contribution prevention notice per completed native wear sweep across frames: piece identity for one, combined count for multiple; capacity-limit warning/recovery and requested coverage reports | Startup settings once in Detailed; observed processing, explicitly labeled roof samples, grouped acquisition/release evidence, wear and ownership evidence in recurring output |

Group records at the producer's logical-operation boundary using operational results, not by comparing already-formatted lines. A batch of torch updates or raid candidate exclusions produces one count; an unsupported-prefab lookup produces one warning with grouped reason counts. Emit nothing for empty or unchanged batches. Flush only completed operations, clear batches on failure/pause/shutdown, and keep new passes separate. Nested native raid scopes share the outer batch. Actual time-skip transitions are naturally single events regardless of the number of sleepers or repeated votes. Do not delay a first failure, a lifecycle transition or a requested report to wait for unrelated messages. Different failures, feature states and named players retain their identities; they are not interchangeable merely because they occur consecutively.

Log results must name the observed operation/outcome and preserve native execution/exception behavior. Shared interpretation and causality limits belong in [server observations and logs](limitations.md#server-observations-and-logs).

### Debug policy startup declaration

Each module emits one Debug LIFECYCLE policy declaration during Initialize when global DebugMode, its own DebugMode or hidden suppression is requested, including disabled modules. DebugLoggingPolicy reports source (global, feature, global_and_feature or hidden_override), both switches, effective configured eligibility, hidden suppression, feature enablement and configuration section. All-off configurations remain quiet. A suppression declaration is the one explicit exception to per-feature debug suppression: it explains why recurring diagnostics and expensive evidence are disabled. It does not enable them or alter loader filters. Ordinary automatic Debug grace still applies. Feature DebugMode false inherits global debug; only DisableDebugLogging suppresses it. BepInEx Console/Disk severity filters must include Debug to display any of these records.

### Empty-server debug pause

`Shared/ServerDebugActivity` supplies one application-wide five-minute grace period for automatic Debug output. It begins at plugin startup and restarts when the last connected peer leaves. `Plugin.Update` refreshes the shared PlayerServices presence snapshot before ticking modules; connecting/loading peers count as present. A connection immediately permits Debug output again, subject to the configured global/feature policy, hidden suppression and ordinary sample budgets. Unknown population does not start a new empty interval, but unavailable server context does not extend the initial startup grace indefinitely. Elapsed time is monotonic; clock rollback resets the grace safely.

`DebugLoggingPolicy.ConfiguredEnabled` represents the loaded debug switches and hidden override; `Enabled` additionally applies the shared activity gate. Diagnostics constructed while paused retain configured eligibility and resume without reinitializing gameplay. Feature diagnostics gate optional snapshots and history collection, and `DiagnosticLog` rejects Debug records before formatting as a final shared safeguard. The pause does not emit recurring suppression notices, change configuration, stop modules or suppress Info, Warning, Error or explicitly requested reports. Existing empty-scope and per-stream limits may already prevent output during the grace period; it is permission for useful startup diagnostics, not a requirement to emit them.

### Repetition and volume limits

These are the target logging requirements. Existing per-stream budgets are retained, but alone do not prevent an unchanged message from filling a log for hours.

| Output | Emission rule |
| --- | --- |
| Routine Info | Once on startup or a meaningful state transition. Do not repeat enabled, ready, waiting or configuration-loaded messages on polls. |
| Recurring Debug summary | At most once per 60 seconds when meaningful state or activity changes. An unchanged, relevant state may emit one compact heartbeat per five minutes; omit unchanged full snapshots. |
| Empty or inactive scope | No routine summary or heartbeat when there are no relevant players/objects or the feature is inactive. A change into or out of that scope can be recorded once at Debug. Unknown scope is distinct from an observed empty scope. |
| Repeated Debug sample | Emit meaningful changes within the existing feature stream caps. Aggregate identical reasons; do not rotate unchanged items indefinitely just to fill the allowance. |
| Warning incident | Report the first occurrence immediately. For the same unresolved issue, emit at most one concise reminder per five minutes, including the number of repeats suppressed. Report recovery once if it changes a user-visible degraded state. |
| Error or exception incident | Report the first failure and its structured evidence immediately, independently of debug settings. For the same unresolved failure, emit at most one compact structured reminder per 60 seconds with repeat counts; include a full stack again only when the failure changes or recurs after recovery. |
| Explicit operator report | Respond to each consumed request regardless of automatic log budgets. At Info, express essential counts, scope and limitations in readable sentences; optional supporting technical details use Debug. |

A meaningful change includes player population or inferred-idle changes, protection/waiting status changes, refill requests or observed outcomes, a new sleep vote/time skip, or a raid evaluation/outcome. Advancing clocks, sample ages, automatic-update invocation counts and identical rejection/no-change results alone do not qualify. Compare semantic state and relevant interval activity, not the fully formatted message or raw lifetime counters. Repeated polling with no new result is neither a new failure nor a new observation. Heartbeats summarize the current state and aggregated repetitions without replaying all metrics.

Retain the per-stream sample caps below and add an overall ceiling of 60 optional Debug records per feature per 60 seconds, including summaries and heartbeats. Within that allowance prioritize new states, changed outcomes and useful blockers over unchanged samples. Warning/Error incidents and requested reports do not consume this Debug allowance. Include capped sample counts in the next permitted Debug summary; do not generate extra lines solely to announce suppression. This caps output only: gameplay state, operational counters and required processing continue.

Deduplication uses stable feature/event/reason identities and relevant object or actor identity where needed, excluding timestamps, volatile counters and raw exception messages containing changing values. Incident identity includes the operation, exception type and stable failure location. Keep retained identities bounded and clear them on feature shutdown; never allow unique object IDs or formatted message text to grow an unbounded cache. Cache churn must still respect output budgets. A changed failure or recovery starts a new incident; a timer expiring alone does not. Feature guides and specifications describe their relevant semantic changes and retained limits.

Each feature emits at most one SUMMARY per 60 seconds. WeatherGuard Medium output contains up to twelve compact qualifying-item protection RESULT samples per summary, prioritizing new/changed status under the semantic deduplication rules above. Its level and optional world-created inclusion are defined in WeatherGuardSettings; global OR feature DebugMode enables diagnostics. Medium skips detailed evidence collection. In Detailed mode, WeatherGuard reserves one processing-counter DETAIL alongside that summary and at most six watched-object RESULT records. OBJECT, ACTION and additional DETAIL categories each allow six samples per 60 seconds; native command RESULT samples have a separate six-message allowance. Wear DETAIL selection also limits each protected/unprotected and exposed/covered group to three distinct candidates per window. InfiniteTorches emits one ordinary Info count per reconciliation pass with observed sufficient fuel updates, never one Info entry per torch; empty/unconfirmed passes stay quiet. InfiniteTorches allows four request ACTION records and six outcome RESULT records per 60 seconds. All pending refill outcomes use its Diagnostics formatter; per-pass output cannot bypass the shared limits. Summaries report `omitted_messages_interval` when event samples exceed their allowance. These are omitted log attempts, not lost gameplay measurements.

Explicit operator reports use Info independently of automatic budgets and Debug settings. Report observed blockers with supporting metadata; feature-specific fields belong in the current specification.

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
