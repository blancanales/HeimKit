# HeimKit architecture

## Scope and current status

HeimKit is a C# BepInEx plugin for Windows Valheim dedicated servers. Supported game, loader and framework versions are documented in [server setup](server-setup.md#supported-environment). The source contains Infinite Torches, WeatherGuard, MajoritySleep, AfkSleepExclusion and AfkRaidProtection.

The current distribution is one plugin DLL with independently controlled modules. Shared code is allowed. Each module must work without another module being enabled. Windows-only support is an explicit product constraint until Linux support is explicitly added.

WeatherGuard is under `src/HeimKit/Features/WeatherGuard/`; see [its specification](specs/weather-guard.md). Its configuration uses `[WeatherGuard]`. Its code moves eligible player-built structural-piece processing to the server, loads supporting context and filters client state while preserving existing creators and native commands. Enabled is its feature switch. World-created claiming is inactive.

NearbyChestCrafting has [research documentation](specs/nearby-chest-crafting.md) only. The inspected normal crafting path is client-local; no supported server-only path has been established. There is no module, registration or configuration toggle. Do not implement it unless server-only feasibility is established.

This document explains the project-wide design. [Contributing](contributing.md) gives the procedure for adding and organizing code. [Feature specs](specs) define individual behavior. [README.md](../README.md) provides the project overview and links to detailed documents. [AGENTS.md](../AGENTS.md) directs contributors to these documents.

## File placement

```text
HeimKit/
  AGENTS.md
  README.md
  LICENSE
  build.ps1
  config/
    heimkit.cfg
  docs/
    architecture.md
    contributing.md
    server-setup.md
    world-backups.md
    development.md
    features/
      infinite-torches.md
      weather-guard.md
    specs/
      infinite-torches.md
      weather-guard.md
  src/
    HeimKit/
      HeimKit.csproj
      Plugin.cs
      Configuration/
        ConfigurationLoader.cs
        DiagnosticsSettings.cs
      Shared/
        IServerModule.cs
      Features/
        WeatherGuard/
          WeatherGuard.cs
          WeatherGuardSettings.cs
          WeatherGuardController.cs
          WeatherGuardDiagnostics.cs
          WeatherGuardPatches.cs
          WeatherGuardRules.cs
          WeatherGuardNetworkFilter.cs
          Models/
            Routing.cs
            WeatherGuardStatus.cs
            WearObservation.cs
            RainEvidence.cs
            WearProcessingState.cs
        InfiniteTorches/
          InfiniteTorches.cs
          InfiniteTorchesController.cs
          InfiniteTorchesSettings.cs
          InfiniteTorchesDiagnostics.cs
          InfiniteTorchesPendingRefills.cs
          Models/
            RefillObservation.cs
            RefillRequest.cs
```

| Responsibility | Location |
| --- | --- |
| BepInEx entry point and module dispatch | `src/HeimKit/Plugin.cs` |
| One module's implementation, settings and helpers | `src/HeimKit/Features/<FeatureName>/` |
| Reusable contracts, log formatting and sample budgets | `src/HeimKit/Shared/` |
| Shared config-file creation, loading and saving | `src/HeimKit/Configuration/` |
| Optional example configuration | `config/heimkit.cfg` |
| Module behavior and acceptance criteria | `docs/specs/<feature-name>.md` |

Use one folder inside Features for each module. Put its class files inside that folder, not directly under Features. Move a helper to Shared only when its responsibility is actually reusable. Shared code must not implicitly enable a module or require another module's private state.

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

Each feature main class owns settings construction, compatibility, lifecycle, startup state and failure isolation. Its controller owns gameplay processing and reports processing failures to the main class. This boundary applies consistently to Infinite Torches and WeatherGuard.

Each module implements `IServerModule`:

| Member | Contract |
| --- | --- |
| `Name` | Human-readable module name for logs. |
| `IsActive` | Whether the module is permitted to perform work. |
| `Initialize(gameVersion)` | Check its own enable setting and compatibility; report effective state. |
| `Tick(now)` | Perform module work only when enabled and compatible; respect scheduling. |
| `Shutdown()` | Stop work and release module resources. Safe to repeat. |

Disabled modules must not scan world objects, send gameplay messages or install active patches. The entry point may tick inactive modules to service explicit local administration. A module must gate gameplay independently; WeatherGuard's requested coverage reports describe inactive/unobserved scope without starting a controller or scanning world objects. Shut down scheduled work and remove any owned patches/subscriptions when those mechanisms are added. Current config edits require a server restart; live toggling is not advertised.

A module error must disable and report that module rather than deliberately disabling unrelated modules. The entry point catches lifecycle failures per module. Construction/registration failures are also caught per module. A failure in the shared configuration setup currently prevents startup and must be reported clearly.

## Configuration

The plugin resolves its config directory from `BepInEx.Paths.ConfigPath`; it must not hard-code a drive letter or search the disk for BepInEx.

Startup order:

1. Check for `heimkit.cfg` in the loader-provided directory.
2. Load existing values, or create an in-memory config if absent.
3. Bind all implemented module settings and defaults with automatic intermediate saves disabled.
4. Save the complete configuration, creating the directory and file as needed.
5. Log whether configuration was created or loaded, including its full path.
6. Initialize modules only after configuration setup succeeds.

Existing recognized values must be preserved, and missing settings added with defaults. A load/save error must be reported without claiming successful setup or enabling affected functionality.

Each module has its own section and `Enabled` setting. Torch settings currently use `[Torches]`. Keep the optional `config/heimkit.cfg` consistent with code defaults, but never require users to manually create or copy it. The built DLL must generate a complete config on first use without the example file.

## Logging

All HeimKit-owned output uses the shared format:

```text
Feature: EVENT | Plain-language result | elapsed_s=value | field=value
```

BepInEx supplies the Info, Warning or Error prefix. Info includes both ordinary state output and optional diagnostics; DETAIL is an event category, not a separate configuration switch or BepInEx severity. Plugin startup reports `log_schema=1` and the application version. `elapsed_s` is monotonic wall-clock time since plugin startup; native game-clock values use separate fields such as `game_time_s` and observed wet duration.

| Event | Meaning |
| --- | --- |
| LIFECYCLE | Startup, enabled/disabled state, readiness, warnings and failures. |
| SUMMARY | Current scope and activity over a reporting interval. |
| OBJECT | First observation, eligibility change or end of observation. |
| ACTION | An operation performed or request sent; delivery is not implied. |
| RESULT | An observed outcome, its measurements and any evaluation blockers. |
| DETAIL | Supporting timing, loading, calculation or network evidence. |

`Shared/DiagnosticLog` owns formatting and severity preservation. Field names contain letters, digits or underscores; project fields use snake_case. Values use invariant numeric formatting without rounding away measured precision, lowercase booleans and `unknown` for unavailable values. Backslashes, pipes, carriage returns and newlines are escaped as `\\`, `\|`, `\r` and `\n`, so each message occupies one physical line and delimiters in values are distinguishable. Never treat unavailable client calculations as zero or infer damage cause from health change alone. Counter suffixes distinguish `_now` snapshots, `_pass` latest-pass counts, `_interval` reporting-window events and `_total` controller-lifetime totals.

Startup, readiness, warnings and errors remain visible with `[Diagnostics] DebugMode = false`. Optional troubleshooting requires that single shared switch and an unsuppressed feature policy; WeatherGuard level and world-item inclusion select diagnostic detail and scope without independently enabling diagnostics. Feature Diagnostics classes own message selection, evidence formatting and debug gating. `Shared/DiagnosticBudget` supplies reusable 60-second sample windows and omitted-message counts. `Configuration/DebugLoggingResolver` resolves the manual-only `DisableDebugLogging` key without binding absent defaults, and `DebugLoggingPolicy` computes global DebugMode AND NOT feature suppression. All current modules use this policy. The key is omitted from generated and reference config; BepInEx preserves manually supplied unbound entries on save. Overrides are cached per loaded ConfigFile and feature section. Invalid/unreadable values suppress debug collection/output and produce one standard warning per section/configuration. The [configuration contract](server-setup.md#hidden-per-feature-debug-suppression) defines inheritance, preservation and restart behavior. Future features must use this resolver.

Controllers collect operational measurements and counters independently of printing limits. Feature suppression also skips optional diagnostic snapshots, roof probes and histories; required ownership, support context and refill tracking continue. A suppressed summary poll is not counted as an omitted event. Standard warnings and errors are not hidden behind debug sampling.

Each feature emits at most one SUMMARY per 60 seconds. WeatherGuard Medium output contains up to twelve compact qualifying-item protection RESULT samples per summary, prioritizing new/changed status and rotating remaining items. Its level and optional world-created inclusion are defined in WeatherGuardSettings; DebugMode remains the shared master switch. Medium skips detailed evidence collection. In Detailed mode, WeatherGuard reserves one processing-counter DETAIL alongside that summary and at most six watched-object RESULT records. OBJECT, ACTION and additional DETAIL categories each allow six samples per 60 seconds; native command RESULT samples have a separate six-message allowance. Wear DETAIL selection also limits each protected/unprotected and exposed/covered group to three distinct candidates per window. Infinite Torches allows four request ACTION records and six outcome RESULT records per 60 seconds. All pending refill outcomes use its Diagnostics formatter; per-pass output cannot bypass the shared limits. Summaries report `omitted_messages_interval` when event samples exceed their allowance. These are omitted log attempts, not lost gameplay measurements.

Log conclusions state precisely what is observed: installed patches are readiness, bypassing a weather branch is processing evidence, replicated fuel increases are state observations, and none of these alone establishes client presentation or a delivery acknowledgement. Explicit server-operator coverage reports use normal requested output independently of automatic debug enablement, suppression and sample budgets; they do not turn diagnostics on. Report the observed blocker, rather than an unexplained collection of booleans or a guessed cause. Keep supporting metadata only where it helps explain the result. Feature-specific measurement contracts belong in their current specifications.

## Shared player activity

PlayerActivitySettings defines shared restart-required thresholds; PlayerActivityTracker computes inferred idle state without Unity dependencies. PlayerActivityService samples connected ready peers and their own valid, network-owned character data, observing movement, replicated body rotation, damage, equipment and emote changes. Idle defaults to five minutes without observed activity, with a separate join/recovery grace. Movement is cumulative from the last activity anchor; passive world movement also counts. Camera-only looking and unsent personal-inventory, map, menu or input actions may be invisible. Idle means inferred from available server data, not verified human absence.

The native replicated crafting animation holds station users active. Authenticated routed chat, animation triggers and native damage/repair/removal/container actions reset activity where observed; housekeeping does not. The observer associates the physical RPC connection with a ready peer and owned character, checks the serialized sender, and restores the packet cursor without modifying bytes, recipients, parameters or native execution. It does not install custom client handlers or claim every client action is visible. A native container-open request holds a ten-second pending grace; an observed container owned by that peer with native InUse remains active until released. It stores at most one target per connected peer and does not load containers, change ownership or read inventories.

Unknown/nonfinite actor data, death, character changes, reconnects, wall-clock rollback and observation gaps longer than three sample intervals plus one second reset inference and grace. Bed occupants remain active actual sleepers. Passive health regeneration does not count as activity. Sampling uses unscaled wall-clock time, so night skipping does not advance the idle clock. Connected actor capacity is 4,096; overflow inserts uncertainty, emits a bounded ordinary warning and retains native decisions. Observer failures clear inference and warn at most once per minute; native packets still execute.

Two independent AFK consumers acquire idempotently disposable leases. The first installs the shared observer; the final release unpatches it and clears player/container state. Disabling sleep exclusion leaves raid tracking alive and vice versa. With both off, no AFK polling or observer remains. MajoritySleep alone reads only the character state needed by the vote, without acquiring idle tracking. World-service changes discard old activity state. Required tracking continues with debug disabled; optional logs do not control gameplay.

SleepVoteCoordinator owns one native EverybodyIsTryingToSleep prefix, with separate majority/exclusion registrations. Removing one retains the other; removing both unpatches voting. Game.UpdateSleeping, Bed.Interact and native SleepStart/SleepStop are unchanged. Without a known living bed occupant, voting is false. With that prerequisite met, unknown actors return to native voting. Controllers own decision/counter observations; Diagnostics classes format one summary per minute. AfkSleepExclusion can include at most six prioritized actor samples per minute. Each sleep module also allows six changed native sleep-result records per minute; optional probes read gates and before/after native state without altering controls. Significant outcomes retain age and counters while samples are capped. See the [sleep evidence contract](specs/majority-sleep.md#diagnostic-evidence). Sleep summaries label the last observed vote and its age; daytime conditions can leave that vote unchanged. A met quorum is not an observed time skip.

AfkRaidProtection owns a scoped automatic-update hook and candidate filter. Only normal random events inside UpdateRandomEvent are considered; standalone, forced/named and explicit admin paths remain native. A candidate is suppressed only when it has nearby reliably observed players and every nearby player is inferred idle, using native horizontal event range and dungeon-height exclusion. Unknown actors prevent suppression. Already running events continue. Candidate results have a six-per-minute allowance; native outcome records and AFK actor samples each have a separate six-per-minute allowance. Read-only native gate, start-return and setter observers distinguish evaluation, request and observed server event state. Summaries expose named invocation/empty/rejection counters, ages, deferred AFK changes and omitted/unchanged samples. See the [raid evidence contract](specs/afk-raid-protection.md#diagnostic-evidence). Each module uses the shared hidden debug suppression policy independently.

## Server-only compatibility

Shared world state and network ownership are different concerns. Inspect both client and server behavior before declaring a module server-only. Server-synced settings alone do not establish support for unmodified clients.

Use vanilla-compatible mechanisms and verify ownership transitions. Do not bypass the current game-version guard merely to make a newer build load. Review changed APIs and update the relevant spec before broadening the supported version.

## Contributor procedures

Follow [contributing.md](contributing.md) to add or change a feature. Each feature owns a settings class, a main lifecycle class and a Models folder when data types are needed. Helpers follow actual responsibilities rather than a fixed template. Shared ConfigurationLoader handles the single config file; DiagnosticsSettings binds the common debug switch. Feature constructors bind settings before the loader saves and Plugin initializes modules.

Build commands and development references are maintained in [development](development.md). Supported versions and installation belong in [server setup](server-setup.md).

## Documentation content

The root README introduces HeimKit's value for users, describes each feature's benefits and high-level expectations, summarizes compatibility and essential limitations, and links to usage guides and specifications under docs. It also contains a documentation index and license information. Detailed operational and technical content stays in its owning document. The canonical content-placement rules are in [contributing](contributing.md#update-documentation).

The [Supported environment section of server setup](server-setup.md#supported-environment) is the sole documentation source for supported version values; all other documents link there, including the README and development references. Server setup also owns shared operational instructions. Development owns build commands and external development references. Each feature guide owns usage and diagnostic interpretation; each feature specification owns implementation behavior and technical contracts. Cross-link these documents rather than copying their detailed content.

Keep one current guide and one current specification per feature. Do not append development journals, abandoned designs, per-release histories, task tracking, completion checkboxes or passed/pending test inventories. Testing instructions, results and validation-status notes belong only in the active chat; follow the [chat-only rule](contributing.md#testing-information-belongs-in-chat-only). Keep architecture and agent files focused on durable project rules. Git provides source history.
