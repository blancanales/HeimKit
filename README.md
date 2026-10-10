# HeimKit

A modular C# utility plugin for Windows Valheim dedicated servers, built with BepInEx 5.

HeimKit aims to offer independently configurable conveniences while letting players connect with unmodified Valheim clients. Each implemented feature must have its own on/off switch.

## Current status

**Early development. No production release is available.**

The working branch is `feature/server-qol`, created from `main`. The implementation has not yet been imported into this repository.

A separate local prototype, originally called Hayman Server QoL 0.1.0, implements periodic torch refilling. It compiled against installed Valheim 1.0.17 dedicated-server assemblies with zero warnings and zero errors. The installed client and server Fireplace classes were also compared and matched.

**Multiplayer testing remains pending.** The prototype has not been tested with connected unmodified clients or installed into the live server. Its source, DLL and build script are not yet committed here. Renaming and integration into HeimKit will establish the final project, DLL and configuration filenames.

## Goals

- Install only on a Windows dedicated server.
- Allow players to join without installing HeimKit or BepInEx on their clients.
- Give every implemented feature its own enable/disable setting.
- Preserve unrelated game behavior when a feature is enabled or disabled.
- Identify unsupported behavior clearly.
- Verify ownership transfers, synchronization, restarts and world reloads.
- Keep game assemblies, server credentials, saves and personal paths out of Git.

## Requested features

| Feature | Current assessment | Status |
| --- | --- | --- |
| Infinite torches | Vanilla fuel-setting messages provide a plausible server-only refilling mechanism. Runtime verification is required. | Implemented in the separate local prototype; not integrated here. |
| Waterproof buildings | A server-local rain-check patch is insufficient when a client owns simulation. Server ownership and incoming-state interception need further investigation. | Open research item; not implemented. |
| Crafting from nearby chests | Vanilla crafting checks and consumes the player's local inventory. A server patch alone does not expand those checks to chest contents. | Blocked for the standard vanilla crafting workflow; not implemented. |

These findings concern the inspected game build and proposed mechanisms. They do not establish that every possible server-only approach is impossible.

## How Valheim ownership affects server-only features

Building health and torch fuel are shared world values stored and distributed through the server. However, the machine calculating a change can be a player's computer.

Objects have a simulation owner, often a connected client. The owner calculates wear or fuel consumption, changes shared network data, and sends changes through the server to other players.

Changing a component flag only on the server therefore may not change the behavior of the client owning that object. A server-only feature must use behavior vanilla clients already understand or correctly move the relevant simulation to the server.

### Infinite torches

The local prototype sends the existing vanilla `RPC_SetFuelAmount(float)` message to an eligible torch's current owner. The owner's existing handler writes ordinary fuel state, which can synchronize to other players.

Prototype behavior:

- Every five seconds, read object records in connected players' near simulation sectors.
- Find eligible allowlisted torches owned by the corresponding player.
- Below 75% fuel, request a refill to the prefab's normal maximum.
- Consume no player resin for the automatic refill.
- Send at most 32 refill requests per pass, rotating across candidates.
- Skip unsupported or missing prefab definitions.
- Run only in the dedicated-server process.
- Disable outside the audited game version, 1.0.17, until reviewed.

Default prototype candidates:

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

### Waterproof buildings

The inspected `WearNTear.UpdateWear` method performs weather wear under an ownership check. Its `m_noRoofWear` field is a local component setting, rather than a verified synchronized immunity switch.

Further investigation must determine whether HeimKit can safely retain server simulation ownership or intercept incoming health changes while preserving combat damage, support, destruction and synchronization.

A health update alone may not identify the reason for damage. Periodic repairs also heal other damage and do not meet the goal of selective weather protection. Rain and water exposure must be assessed separately where game logic differs.

The older [Serverside Simulations project](https://github.com/ddormer/valheim-serverside) demonstrates the simulation-ownership option. Its documented version and maintenance status do not establish compatibility with this build, and it is not a dependency of HeimKit.

### Crafting from nearby chests

In the inspected code, `Player.HaveRequirementItems` counts local player inventory, and `Player.ConsumeResources` removes materials from it. Patching these on a dedicated server does not change the unmodified client's recipe checks.

A viable alternative must prove that the vanilla client can initiate and complete the action and that simultaneous players cannot duplicate resources or bypass chest access permissions.

Inventory injection, free crafting and automatic chest transfers are not equivalent to crafting directly from nearby chests. None is implemented.

See the [GorilaChestMod author's client/server explanation](https://github.com/trentinidev/GorilaChestMod#server-side) for another implementation explicitly separating client crafting from server features.

## Independent feature switches

Required configuration design:

| Feature | Intended switch | Current status |
| --- | --- | --- |
| Torch refilling | `[Torches] Enabled` | Exists in the local prototype. |
| Waterproof protection | `[WaterproofBuildings] Enabled` | Reserved design; unavailable. |
| Chest crafting | `[CraftFromChests] Enabled` | Reserved design; unavailable. |

Every implemented module must check its own setting before doing work. Disabling one must not disable unrelated modules.

Reserved switches are **not functional configuration options today**. Unsupported features must never silently appear enabled. If exposed later, their unavailable status must be explicit.

Actual local prototype settings:

```ini
[Torches]
Enabled = true
IntervalSeconds = 5
MaxMessagesPerPass = 32
Prefabs = piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch

[Diagnostics]
LogRefillRequests = false
```

The legacy prototype config is `BepInEx/config/trevorhayman.valheim.serverqol.cfg`. The final HeimKit filename will be documented after integration. Stop the server before edits and restart afterward.

Diagnostic counts indicate requests sent, not delivery acknowledgements.

## Requirements and inspected baseline

- Windows Valheim Dedicated Server 1.0.17.
- Matching unmodified clients for testing.
- BepInExPack Valheim 5.4.2351, containing BepInEx 5.4.23.5.
- A .NET SDK for building source.
- Target framework: `netstandard2.1`.
- Local server managed assemblies and BepInEx core DLL references.

The torch prototype uses neither Harmony nor Jotunn.

Inspection on October 10, 2026 recorded:

| Evidence | Value |
| --- | --- |
| Installed server Steam build | `25730807` |
| Installed client Steam build | `25730771` |
| Assembly game version | `1.0.17` |
| Server assembly_valheim.dll SHA-256 | `0DFC7E81436F822121148EFED859BA1E661D58B9484BD45E3B6F33B6DD86BFAD` |

Code inspection confirmed that the vanilla Fireplace registers a float fuel-setting RPC and that its handler checks object ownership. It also confirmed owner-side fuel consumption and building wear. Future patches, crossplay and actual runtime delivery still need testing.

## Building

**These commands apply to the separate local prototype. A fresh clone cannot run them until source and build files are imported.**

From the prototype folder:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server'
```

For an extracted development copy of BepInEx:

```powershell
.\build.ps1 -ServerPath 'C:\path\to\Valheim dedicated server' -BepInExCore 'C:\path\to\BepInEx\core'
```

Alternatively:

```powershell
dotnet build .\HaymanServerQoL.csproj -c Release '-p:ServerPath=C:\path\to\Valheim dedicated server'
```

Prototype output: `bin/Release/netstandard2.1/HaymanServerQoL.dll`.

Integration must rename project and plugin identifiers consistently, and replace these instructions with commands verified against the committed HeimKit source.

## Installing a test build

No HeimKit release DLL is currently available from this repository. Once a build is supplied:

1. Stop the dedicated server normally.
2. Back up world/save data and launch configuration.
3. Use a separate test world and save directory for initial testing.
4. Download the [Valheim BepInEx pack](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
5. Extract outside the server folder, then copy the contents of `BepInExPack_Valheim` beside `valheim_server.exe`.
6. Place the supplied plugin DLL in a subfolder of the server's `BepInEx/plugins`.
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

All runtime checks remain pending:

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
- [ ] Unimplemented features cause no unintended changes.
- [ ] Removing the plugin leaves ordinary objects and data usable.
- [ ] Steam and crossplay are tested separately if both are advertised.

Record game version, server build, plugin version, network backend, settings, observed fuel values and relevant logs.

## Troubleshooting

**Plugin does not load:** Confirm BepInEx is beside the dedicated-server executable and read its log. Check the installed game and runtime versions.

**Version guard disables the plugin:** Review changed APIs and repeat multiplayer tests before updating the guard. Successful compilation alone does not validate network behavior.

**Torch does not refill:** Check eligibility warnings, prefab name, owner and whether the object is loaded. Large clusters may take several passes. Water or smoke can extinguish a fueled torch.

**Request logged but no visible change:** The log confirms attempted transmission. The owner may have changed or the object may not have loaded when the message arrived.

**Waterproofing or chest crafting unavailable:** These modules are not implemented; their feasibility remains documented above.

Remove passwords, join codes and other credentials before sharing logs.

## Disabling and removing

Stop the server, disable a feature in its configuration, and restart to test the change. To remove the plugin, stop the server and remove its DLL before restarting.

The current torch design writes ordinary fuel values. Removing it leaves those values to burn normally. Restore the backup if exact original fuel amounts are required.

## Contributing

Work begins on `feature/server-qol`. Submit focused pull requests describing player-visible behavior, why it works with unmodified clients, ownership considerations, tests and remaining limitations. Update configuration and installation documentation with code changes.

Track source, project files, build scripts and documentation. Do not commit copied game assemblies, BepInEx runtime files, world saves, credentials, machine-specific paths or generated `bin/` and `obj/` output. Distribute tested plugin builds as release artifacts.

Server-synced configuration is not proof of server-only execution. Verify the actual client/server path.

## License

HeimKit uses the [MIT License](LICENSE). The current license names `blancanales` as copyright holder.

Valheim files and third-party dependencies retain their own licenses and are not relicensed or distributed by this project.

## References

- [BepInEx plugin development guide](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/2_plugin_start.html)
- [Valheim BepInEx pack and installation](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [Serverside Simulations architectural reference](https://github.com/ddormer/valheim-serverside)
- [GorilaChestMod client/server explanation](https://github.com/trentinidev/GorilaChestMod#server-side)
