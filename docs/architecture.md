# HeimKit architecture and development rules

## Scope and current status

HeimKit is a C# BepInEx 5 plugin for Windows Valheim dedicated servers. It currently contains one implemented module: infinite torches. The source targets `netstandard2.1`; the inspected game baseline is Valheim 1.0.17. Wooden-torch refilling passed an initial live test on October 10, 2026; broader multiplayer validation remains pending.

The current distribution is one plugin DLL with independently controlled modules. Shared code is allowed. Each module must work without another module being enabled. Windows-only support is an explicit product constraint until Linux support is requested, implemented and tested.

This document defines project-wide rules. [Feature specs](specs) define individual behavior. [README.md](../README.md) explains installation and implemented functionality to users. [AGENTS.md](../AGENTS.md) directs coding agents to these documents; [CLAUDE.md](../CLAUDE.md) points only to AGENTS.md.

## File placement

```text
HeimKit/
  AGENTS.md
  CLAUDE.md
  README.md
  LICENSE
  build.ps1
  config/
    heimkit.cfg
  docs/
    architecture.md
    specs/
      infinite-torches.md
  src/
    HeimKit/
      HeimKit.csproj
      Plugin.cs
      Configuration/
        ConfigurationLoader.cs
      Shared/
        IServerModule.cs
      Features/
        InfiniteTorches/
          InfiniteTorches.cs
          TorchDiagnostics.cs
  tests/
    HeimKit.LifecycleChecks/
```

| Responsibility | Location |
| --- | --- |
| BepInEx entry point and module dispatch | `src/HeimKit/Plugin.cs` |
| One module's implementation, settings and helpers | `src/HeimKit/Features/<FeatureName>/` |
| Reusable contracts and utilities | `src/HeimKit/Shared/` |
| Shared config-file creation, loading and saving | `src/HeimKit/Configuration/` |
| Optional example configuration | `config/heimkit.cfg` |
| Module behavior and acceptance criteria | `docs/specs/<feature-name>.md` |
| Automated validation | `tests/` |

Use one folder inside Features for each module. Put its class files inside that folder, not directly under Features. Move a helper to Shared only when its responsibility is actually reusable. Shared code must not implicitly enable a module or require another module's private state.

## Naming

- Class and interface names do not contain the word `Feature`.
- Use names that describe responsibility, such as `InfiniteTorches`, `ConfigurationLoader` and `IServerModule`.
- Match filenames to their primary class/interface names.
- Use `HeimKit.Features.<FeatureName>` for module namespaces, and `HeimKit.Shared` or `HeimKit.Configuration` for shared code.
- The Features folder name remains intentional; the naming restriction applies to class/interface names.
- Use the plugin identifier `heimkit`. The user-facing configuration filename is independently defined as `heimkit.cfg`. The identifier was changed from `blancanales.heimkit` at Trevor's request; configuration loading uses an explicit path and is unaffected by that change.

## Module lifecycle and isolation

`Plugin.cs` registers modules, initializes them, dispatches updates to active modules and shuts them down. It should not contain torch selection, refilling or other module-specific gameplay algorithms.

Each module implements `IServerModule`:

| Member | Contract |
| --- | --- |
| `Name` | Human-readable module name for logs. |
| `IsActive` | Whether the module is permitted to perform work. |
| `Initialize(gameVersion)` | Check its own enable setting and compatibility; report effective state. |
| `Tick(now)` | Perform module work only when enabled and compatible; respect scheduling. |
| `Shutdown()` | Stop work and release module resources. Safe to repeat. |

Disabled modules must not scan world objects, send gameplay messages or install active patches. Shut down scheduled work and remove any owned patches/subscriptions when those mechanisms are added. Current config edits require a server restart; live toggling is not advertised.

A module error must disable and report that module rather than deliberately disabling unrelated modules. The entry point catches lifecycle failures per module. As modules are added, isolate construction/registration failures too. A failure in the shared configuration setup currently prevents startup and must be reported clearly.

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

DebugMode is the single troubleshooting switch. Status explanations and detailed-pass samples are automatically limited to 60 seconds per stream. Keep counters scoped to examined objects and the current pass. Include operation context and skip/rejection reasons without claiming unobserved client state. Keep module-specific formatting in the module folder. Remove retired diagnostic category settings when saving older configurations, preserving the existing DebugMode value.

## Server-only compatibility

Shared world state and simulation ownership are different concerns. Inspect both client and server behavior before declaring a module server-only. Server-synced settings alone do not establish support for unmodified clients.

Use vanilla-compatible mechanisms and verify ownership transitions. Do not bypass the current game-version guard merely to make a newer build load. Review changed APIs, update the relevant spec and repeat testing before broadening the supported version.

## Adding an authorized module

1. Create `docs/specs/<feature-name>.md` with scope, feasibility evidence, exact behavior, ownership, configuration, logging, failure handling and acceptance criteria.
2. Create `src/HeimKit/Features/<FeatureName>/` and its classes using the naming rules above.
3. Implement the lifecycle contract and its independent enable switch.
4. Register it in the entry point and bind its settings before the shared config is saved. Preserve per-module failure isolation.
5. Add only genuinely reusable helpers to shared locations.
6. Update the optional example config with implemented settings; do not add misleading switches for unimplemented behavior.
7. Verify the module alone, with all other modules disabled, then relevant combinations and failure scenarios. For three implemented independent switches, test all eight combinations.
8. Build, run meaningful automated checks and perform the required vanilla-client acceptance tests.
9. Add it to the README only when it works and is verified. Keep research and incomplete work in specs/development documentation.

## Build and validation

From the repository root, with BepInEx installed in the server directory:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

For references extracted elsewhere:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

Run the lifecycle/config checks with a .NET 10 SDK:

```powershell
dotnet run --project .\tests\HeimKit.LifecycleChecks -c Release '-p:ServerPath=C:\path\to\Valheim dedicated server' '-p:BepInExCore=C:\path\to\BepInEx\core'
```

Use real local references. Do not commit them or machine-specific paths. Build output is `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll`. The example config copied beside it is optional. Do not distribute test-runner output containing copied reference DLLs.

Run checks relevant to the change. Documentation-only edits need link/content validation rather than an unnecessary gameplay rebuild. Report compilation, automated checks and live multiplayer validation separately. Unchecked acceptance items remain unchecked until observed.
