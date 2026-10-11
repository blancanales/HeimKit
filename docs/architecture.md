# HeimKit architecture

## Scope and current status

HeimKit is a C# BepInEx plugin for Windows Valheim dedicated servers. Supported game, loader and framework versions are documented in [server setup](server-setup.md#supported-environment). The source contains Infinite Torches and WeatherGuard.

The current distribution is one plugin DLL with independently controlled modules. Shared code is allowed. Each module must work without another module being enabled. Windows-only support is an explicit product constraint until Linux support is explicitly added.

WeatherGuard is under `src/HeimKit/Features/WeatherGuard/`; see [its specification](specs/weather-guard.md). Its configuration uses `[WeatherGuard]`. Its code moves eligible player-built structural-piece processing to the server, loads supporting context and filters client state while preserving existing creators and native commands. Enabled is its feature switch. World-created claiming is inactive.

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
| Reusable contracts and utilities | `src/HeimKit/Shared/` |
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

Disabled modules must not scan world objects, send gameplay messages or install active patches. Shut down scheduled work and remove any owned patches/subscriptions when those mechanisms are added. Current config edits require a server restart; live toggling is not advertised.

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

Startup reports each module's effective `ENABLED` or `DISABLED` state. State logs include compatibility or failure reasons when relevant. Enabled means permitted to run, not proof that a world object was found or a client applied a message.

Standard logging includes config creation/loading, module state, readiness, warnings and errors. Optional troubleshooting output requires `[Diagnostics] DebugMode = true`. This master switch defaults false and gates request counts, status explanations and detailed samples. There are no separate diagnostic category switches. Diagnostic messages use Info level for visibility in normal BepInEx output. They are not a separate Debug-level mode. Messages about network requests must distinguish transmission attempts from observed delivery.

DebugMode is the single troubleshooting switch. Status explanations and detailed-pass samples are automatically limited to 60 seconds per stream. Distinguish current-pass counts from cumulative totals. Torch pass counts describe examined candidates; WeatherGuard transition and guard counters are cumulative for the controller lifetime. Include operation context and skip/rejection reasons without claiming unobserved client state. Keep recurring debug formatting, gating and rate limits in `<FeatureName>Diagnostics`; controllers retain processing counters and supply data without moving gameplay behavior into diagnostics. Standard event logs stay at their event sites. Do not create empty diagnostics classes for features without recurring troubleshooting output. Remove retired diagnostic category settings when saving older configurations, preserving the existing DebugMode value.

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
