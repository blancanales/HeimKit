# HeimKit

Infinite torches for Windows Valheim dedicated servers, built with C# and BepInEx 5.

**Supported platform: Windows only.** Linux support is not implemented or tested. HeimKit currently targets the Windows dedicated-server process, `valheim_server.exe`.

HeimKit automatically refills eligible torches without consuming player resin. It uses the existing vanilla fuel-setting network message so players can use unmodified clients by design. Wooden torch refilling has passed an initial live server test; broader multiplayer validation remains pending.

## Quick configuration guide

Install the DLL and start the dedicated server. HeimKit uses BepInEx's runtime `Paths.ConfigPath` to locate the correct configuration directory, creates `heimkit.cfg` there if missing, and fills it with all default settings. No manual file creation or copying is required. The supplied [config/heimkit.cfg](config/heimkit.cfg) is an optional reference. HeimKit prints the full configuration file path and editing instructions in its startup output and `BepInEx/LogOutput.log`.

To turn infinite torches on or off:

1. Stop the dedicated server.
2. Open `BepInEx/config/heimkit.cfg` in the server installation folder.
3. Under `[Torches]`, set `Enabled = true` to enable refilling or `Enabled = false` to disable it.
4. Save the file and restart the server.
5. Check the startup log for `Infinite Torches: ENABLED` or `Infinite Torches: DISABLED`. If disabled because of an unsupported game version, the log explains why.

## Current status

**Early development. No production release is available.**

The working branch is `feature/server-qol`, created from `main`. The torch implementation is included under `src/HeimKit`, with the build script, example config and checks in this repository.

The current HeimKit implementation provides periodic torch refilling. It compiled against installed Valheim 1.0.17 dedicated-server assemblies with zero warnings and zero errors. The installed client and server Fireplace classes were also compared and matched.

**Initial live refill test passed on October 10, 2026.** On a Windows Valheim 1.0.17 dedicated server running BepInEx 5.4.23.5, logs showed 52 eligible torches and batches of 32 and 20 refill requests. Trevor confirmed that empty wooden torches lit up and displayed 4/4 fuel. Other torch types, inventory consumption, two-client agreement, ownership transfers, restarts and explicit verification of the client's mod state remain unverified. The assembly is `HeimKit.dll` and its BepInEx plugin identifier is `heimkit`.

## Infinite torches

The local prototype sends the existing vanilla `RPC_SetFuelAmount(float)` message to an eligible torch's current owner. The owner's existing handler writes ordinary fuel state, which can synchronize to other players.

Current behavior:

- Every five seconds, read object records in connected players' near simulation sectors.
- Find eligible allowlisted torches owned by the corresponding player.
- Below 75% fuel, request a refill to the prefab's normal maximum.
- Consume no player resin for the automatic refill.
- Send at most 32 refill requests per pass, rotating across candidates.
- Track pending refills by object ID across all players, suppressing duplicate requests while awaiting updated fuel. Retry after 30 seconds without sufficient fuel progress, or re-evaluate after an owner change.
- Skip unsupported or missing prefab definitions.
- Run only in the dedicated-server process.
- Disable outside the audited game version, 1.0.17, until reviewed.

Default candidate prefabs:

```text
piece_groundtorch_wood
piece_groundtorch
piece_groundtorch_green
piece_groundtorch_blue
piece_walltorch
```

Runtime checks require a refillable fireplace with finite positive capacity, no wet low/high flame objects, no toggle-off behavior and no existing infinite-fuel flag. Candidate names are checked against actual prefab data at runtime.

This is **periodic automatic refilling**, not a guarantee of uninterrupted light. Vanilla water, obstruction and smoke rules remain. Campfires, hearths, braziers, furnaces, candles and cooking fires are outside the default scope.

Objects that are unloaded or change owners can ignore a request. Later passes retry. Empty torches may briefly remain dark after loading. Normal refill effects can appear. Manual refilling at the same moment can still consume a player's resin.

Version 0.1.1 adds pending-request tracking. The server checks replicated fuel for a sufficient increase after each request; that observation is not a client delivery acknowledgement or proof that the plugin caused the increase. Missing objects are cleared from tracking; still-unresolved requests expire after 30 seconds, evaluated on the next pass. Retries remain within the server-wide request budget. Tracking is capped at 2,048 objects; new requests wait if that cap is reached rather than bypassing duplicate suppression. Late network delivery can still overlap a timeout retry; this is not an exactly-once protocol.

The original live test used 0.1.0. The new duplicate-suppression behavior in 0.1.1 is covered by automated checks but still needs a multiplayer live test.

## Configuration and on/off switch

Set [Torches] Enabled to false to stop automatic refilling. Stop the server before editing and restart afterward.

Current settings:

```ini
[Torches]
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch

[Diagnostics]
DebugMode = false
```

HeimKit checks for `heimkit.cfg` in the configuration directory supplied by BepInEx. In a normal server installation this is `BepInEx/config/heimkit.cfg`. It binds all settings before saving, creates the directory and file if missing, and logs the full path. Existing setting values are preserved; missing settings are added with defaults. Retired per-category diagnostic settings are removed automatically; the existing DebugMode value is preserved. The supplied [config/heimkit.cfg](config/heimkit.cfg) is an optional reference. Stop the server before edits and restart afterward.

Diagnostic counts indicate requests sent, not delivery acknowledgements.

### Diagnosing refill problems

The plugin creates `[Diagnostics] DebugMode = false` automatically. Set it to `true` and restart for all troubleshooting output: request counts, status explanations, skip counters and sampled per-torch details. Set it to `false` for general startup/configuration, state, readiness, warnings and errors. There are no separate category switches. Diagnostic messages use Info level so the normal BepInEx log filters show them.

Status explanations cover missing world services, the wrong network session, no supported prefab definitions, no ready players, no eligible nearby client-owned torches, sufficient fuel, skip reasons and candidates deferred by the message budget. Counts describe the current pass, not the entire interval or whole world. Off, invalid-fuel and unavailable-owner counts describe examined candidates; deferred torches have not yet been examined.

Status explanations and detailed refill samples are automatically limited to one reporting pass every 60 seconds per stream. Detailed samples are also bounded by MaxMessagesPerPass. When debug mode is on, request-count messages can appear on every pass that sends refills.

Example output with DebugMode = true (values are illustrative):

```text
Infinite Torches status: No ready connected players with character IDs. No requests sent this pass.
Infinite Torches status: All examined torches have enough fuel (at least 75%). No requests sent this pass. Ready players=1; candidates=4; examined=4; sent=0; sufficient fuel=4; off=0; invalid fuel=0; owner unavailable=0; deferred=0.
Infinite Torches refill request: object=<object ID>; prefab=piece_walltorch; owner=<owner ID>; position=<position>; fuel before=0.000; target=6.000; RPC=RPC_SetFuelAmount. Sent to routing; client application unconfirmed.
```

Prefab warnings state the rejection reason. Runtime errors include the operation being attempted and, for refill requests, the affected object, prefab, owner, position and fuel values. Provide startup/version logs, at least two diagnostic intervals and the visible client fuel/light behavior when reporting a problem. Logs do not acknowledge client delivery; an observed fuel increase remains the gameplay check.

### Startup logs

At plugin startup, HeimKit reports the torch feature's effective state:

```text
HeimKit 0.1.1 starting.
Created default configuration: <server folder>\BepInEx\config\heimkit.cfg. All feature settings are included.
To enable or disable Infinite Torches: stop the server, edit [Torches] Enabled = true or false in heimkit.cfg, save, then restart the server.
Infinite Torches: ENABLED (Torches.Enabled = true; interval 5s; maximum 32 refill requests per pass; debug mode False; automatic diagnostic interval 60s). Waiting for the dedicated-server world.
```

On later starts, the configuration message is:

```text
Loaded configuration: <server folder>\BepInEx\config\heimkit.cfg. Existing settings preserved; missing settings added with defaults.
```

If configuration cannot be loaded or saved, the log identifies the path and error and the feature stays disabled.

With `Enabled = false`:

```text
Infinite Torches: DISABLED (Torches.Enabled = false).
```

An unsupported game version produces a `DISABLED` warning with the version and reason, even when configuration requests enabling the feature. `ENABLED` means the module is permitted to run; world readiness and successful refill delivery are separate checks. A runtime failure disables the torch module and logs the error.

## Source layout

```text
src/HeimKit/
  Plugin.cs
  Configuration/
    ConfigurationLoader.cs
  Shared/
    IServerModule.cs
  Features/
    InfiniteTorches/
      InfiniteTorches.cs
      TorchDiagnostics.cs
      PendingRefills.cs
```

`Plugin.cs` loads features, dispatches updates and handles shutdown. `ConfigurationLoader` checks the runtime configuration path, binds all defaults and saves the complete configuration before feature initialization. `InfiniteTorches` owns the torch configuration, version check, scheduling, prefab selection and refill behavior. `IServerModule` provides the shared lifecycle contract. Each feature has its own folder under Features containing its class files. Class names do not include the word Feature. Shared contracts and utilities live outside those individual folders. Each additional working feature must have its own enable setting. Feature failures are handled individually.

## Requirements and inspected baseline

- **Windows only. Linux is not supported by this version.**
- Windows Valheim Dedicated Server 1.0.17.
- Matching unmodified clients for testing.
- BepInExPack Valheim 5.4.2351, containing BepInEx 5.4.23.5.
- A .NET SDK for building source.
- Target framework: `netstandard2.1`.
- Local server managed assemblies and BepInEx core DLL references.

HeimKit uses neither Harmony nor Jotunn.

Inspection on October 10, 2026 recorded:

| Evidence | Value |
| --- | --- |
| Installed server Steam build | `25730807` |
| Installed client Steam build | `25730771` |
| Assembly game version | `1.0.17` |
| Server assembly_valheim.dll SHA-256 | `0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD` |

Code inspection confirmed that the vanilla Fireplace registers a float fuel-setting RPC and that its handler checks object ownership. It also confirmed owner-side fuel consumption. Future patches, crossplay and actual runtime delivery still need testing.

## Building

**Check out feature/server-qol and run these commands from the HeimKit repository root.**

From the repository root:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

For an extracted development copy of BepInEx:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

Alternatively:

```powershell
dotnet build .\src\HeimKit\HeimKit.csproj -c Release '-p:ServerPath=C:\path\to\Valheim dedicated server'
```

Build output: `src/HeimKit/bin/Release/netstandard2.1/HeimKit.dll`.

The build includes an optional reference `heimkit.cfg` alongside the DLL. Install only `HeimKit.dll`; the plugin creates its actual server config automatically on first use. Edit that generated file to change settings. Local reference paths are supplied at build time. No game or BepInEx assemblies are bundled.

### Configuration and startup checks

The lifecycle check runner requires a .NET 10 SDK and local reference DLLs:

```powershell
dotnet run --project .\tests\HeimKit.LifecycleChecks -c Release '-p:ServerPath=C:\path\to\Valheim dedicated server'
```

Supply `-p:BepInExCore` if the development loader is elsewhere. The checks exercise enabled/disabled configuration, supported/unsupported version combinations, startup messages and repeated shutdown. The four lifecycle scenarios passed locally, along with checks for the supplied config, automatic first-use creation, existing-setting preservation, missing-default insertion and setup failures. They do not test live networking.

## Installing a test build

No HeimKit release DLL is currently available from this repository. Once a build is supplied:

1. Stop the dedicated server normally.
2. Back up world/save data and launch configuration.
3. Use a separate test world and save directory for initial testing.
4. Download the [Valheim BepInEx pack](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
5. Extract outside the server folder, then copy the contents of `BepInExPack_Valheim` beside `valheim_server.exe`.
6. Place the supplied plugin DLL in a subfolder of the server's `BepInEx/plugins`. No config installation is required; the plugin creates it automatically on first use.
7. Start the test server using its normal launch method.
8. Check `BepInEx/LogOutput.log` for startup, version checks, skipped prefabs and errors.
9. Join with clean unmodified clients and complete the acceptance checks below.
10. Stop the server before replacing its DLL or editing configuration.

Inspect an existing loader before replacing it. Keep BepInEx versions consistent and follow the Valheim pack's instructions.

Typical loader layout:

```text
Valheim dedicated server/
  valheim_server.exe
  winhttp.dll
  doorstop_config.ini
  BepInEx/
    core/
    plugins/
    config/
```

## Multiplayer acceptance checklist

Initial live evidence: the plugin loaded, generated configuration automatically, sent refill requests and wooden torches visibly lit at 4/4 fuel. The remaining checks below require separate verification:

- [ ] Loads in the dedicated-server process and stays inactive in a game client.
- [ ] Clean unmodified clients can connect.
- [ ] Low-fuel and empty eligible torches refill without spending inventory materials.
- [ ] Two clients observe matching fuel and light.
- [ ] Works after the owner leaves, disconnects or transfers ownership.
- [ ] Works after leaving and returning to an unloaded area.
- [ ] Restart preserves normal data and resumes refilling.
- [ ] Vanilla water, smoke and obstruction behavior remains.
- [ ] Non-allowlisted fires keep normal behavior.
- [ ] Disabling refilling stops requests after restart.
- [ ] Large groups progress without unacceptable server load.
- [ ] Removing the plugin leaves ordinary objects and data usable.
- [ ] Steam and crossplay are tested separately if both are advertised.

Record game version, server build, plugin version, network backend, settings, observed fuel values and relevant logs.

## Troubleshooting

**Plugin does not load:** Confirm BepInEx is beside the dedicated-server executable and read its log. Check the installed game and runtime versions.

**Version guard disables the plugin:** Review changed APIs and repeat multiplayer tests before updating the guard. Successful compilation alone does not validate network behavior.

**Torch does not refill:** Check eligibility warnings, prefab name, owner and whether the object is loaded. Large clusters may take several passes. Water or smoke can extinguish a fueled torch.

**Request logged but no visible change:** The log confirms attempted transmission. The owner may have changed or the object may not have loaded when the message arrived.

Remove passwords, join codes and other credentials before sharing logs.

## Disabling and removing

To disable automatic refilling, stop the server, set [Torches] Enabled = false, and restart. To remove the plugin, stop the server and remove its DLL before restarting.

The current torch design writes ordinary fuel values. Removing it leaves those values to burn normally. Restore the backup if exact original fuel amounts are required.

## Documentation policy

The README covers infinite torches and clearly states its validation status. Add each new feature only when it works and has been verified. Keep research ideas and unimplemented features in separate development notes.

## Contributing

Read [AGENTS.md](AGENTS.md) and [the architecture rules](docs/architecture.md) before changing the project. The [infinite torches specification](docs/specs/infinite-torches.md) defines the current module's behavior and acceptance criteria.

Work begins on `feature/server-qol`. Submit focused pull requests describing player-visible behavior, why it works with unmodified clients, ownership considerations, tests and remaining limitations. Update configuration and installation documentation with code changes.

Track source, project files, build scripts and documentation. Do not commit copied game assemblies, BepInEx runtime files, world saves, credentials, machine-specific paths or generated `bin/` and `obj/` output. Distribute tested plugin builds as release artifacts.

Server-synced configuration is not proof of server-only execution. Verify the actual client/server path.

## License

HeimKit uses the [MIT License](LICENSE). The current license names `blancanales` as copyright holder.

Valheim files and third-party dependencies retain their own licenses and are not relicensed or distributed by this project.

## References

- [BepInEx plugin development guide](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/2_plugin_start.html)
- [Valheim BepInEx pack and installation](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
