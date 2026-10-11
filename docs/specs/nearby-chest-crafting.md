# NearbyChestCrafting specification

## Purpose and implementation restriction

The researched idea would let a player craft an otherwise valid recipe using resources held in permitted nearby chests, reducing manual inventory transfers. The user requires server-only operation with unmodified clients: if that cannot be implemented, do not implement the feature. No supported server-only path has been established in the inspected normal crafting workflow.

This document contains research and possible design boundaries only. No module, toggle, registration, lifecycle scaffold, patches, scans, inventory operations or custom RPCs are included in HeimKit for this feature. It is not an available capability and has no configuration section. Existing features remain independent.

The relevant installed game/dependency baseline is the [supported environment](../server-setup.md#supported-environment). This identifies the assembly inspected, not a compatibility promise for chest crafting. No dependency or client installation is added.

## Evidence sources and inspected native paths

The primary local source is the installed supported server's `valheim_server_Data/Managed/assembly_valheim.dll`, inspected with the [ILSpy decompiler](https://github.com/icsharpcode/ILSpy). Decompiled C# is a reconstruction of the compiled assembly. Method names below identify the inspected paths; no proprietary code or assemblies are distributed in this repository. Reinspect these methods when the compatibility baseline changes.

| Observed native fact | Method/path | Design implication |
| --- | --- | --- |
| Available recipes depend on the player's known recipes, station, DLC and current recipe/season settings. | Player.GetAvailableRecipes | Nearby resources must not bypass recipe discovery or station restrictions. |
| Resource checks count the player's own inventory, including native quality and amount rules. | Player.HaveRequirements(Recipe), HaveRequirementItems | Shared chest data alone does not satisfy a vanilla client's resource check. |
| The craft button is gated by player requirements and usable station; displayed resource counts also use the player's inventory. | InventoryGui.UpdateRecipe, SetupRequirement | The missing-ingredient client UI blocks initiating ordinary crafting even if a server can see chest items. |
| The GUI uses Player.m_localPlayer and returns when it is absent. | InventoryGui.Update | Patching these methods only on a dedicated server does not change a remote client's UI. |
| Crafting rechecks requirements, requires space for the result, creates/adds the item locally, then consumes resources from the player's inventory. | InventoryGui.DoCrafting, Player.ConsumeResources | Item creation and resource consumption are client-local in the reviewed normal crafting path. No dedicated server craft-transaction RPC was found in these paths. This is a scoped finding, not proof that every network message in the game has been ruled out. |
| Player inventory has Save/Load serialization; it is a different Inventory instance from a chest. | Player.Save/Load, InventoryGui.DoCrafting | Editing a server-side chest snapshot does not itself edit the remote client's local player inventory. A usable player-inventory transfer/acknowledgement mechanism remains unestablished. |
| Container inventory changes save only when the local instance owns the container; inventory bytes are stored in the items world-data field. Load follows data revision and skips while the container is in use. | Container.OnContainerChanged, Save, Load | A readable inventory object or replicated byte snapshot is not authority to modify it; stale owner snapshots can overwrite writes. |
| Opening is requested from the owner, checks in-use/private access and transfers ownership to the requester on approval. Take-all/stack also coordinate ownership. | Container.RPC_RequestOpen, RPC_RequestTakeAll, RPC_RequestStack | Opening, depositing and another player's interaction can race any proposed chest withdrawal. Existing interaction RPCs do not provide a multi-chest crafting transaction. |
| Container privacy is public, creator-only private, or denied group in the inspected CheckAccess implementation. | Container.CheckAccess | Public/private/group policy must be specified without silently treating network ownership as permission. This describes native code, not an adopted feature permission policy. |
| Ward checks happen in local interaction, and HaveLocalAccess references the local player. | Container.Interact, PrivateArea.CheckAccess, HaveLocalAccess | Dedicated-server permission evaluation cannot blindly call this local-player path. Server-side actor identity and ward evaluation must be designed explicitly. |
| The normal open UI closes beyond its auto-close distance. | InventoryGui.UpdateContainer | Native interaction distance is not an approved resource-pull radius or discovery algorithm. |
| Removing items by name can consume a partial quantity and returns no full-payment success result. Inventory changes notify/save separately. | Inventory.RemoveItem(string, int, ...), Changed, Save/Load | Count-then-remove is not atomic payment across chests and player inventory; it needs explicit preconditions, reservation and outcome accounting. |

## Public primary-source examples

[AzuCraftyBoxes author documentation](https://thunderstore.io/c/valheim/p/Azumatt/AzuCraftyBoxes/) describes client installation checks when installed on a server and server/client configuration synchronization. Those are client-participating mechanisms, not proof of operation with unmodified clients.

[CraftFromContainers author source](https://raw.githubusercontent.com/aedenthorn/ValheimMods/master/CraftFromContainers/BepInExPlugin.cs) patches InventoryGui and Player requirement paths; its nearby-container lookup returns an empty set without Player.m_localPlayer. This reinforces the client-participation distinction. The source is an architectural example, not a compatibility guarantee or approved implementation to copy. Neither project is a HeimKit dependency.

[CraftFromContainersEvolved author documentation](https://thunderstore.io/c/valheim/p/KodeMansMods/CraftFromContainersEvolved/) explicitly distinguishes server-side access enforcement from container crafting: unmodified clients can be subject to its server gate without gaining chest crafting. It also says a generic write gate can affect manual looting because the server cannot identify every write as a scripted crafting pull. Such a gate would be a permission change, not a transparent crafting implementation.

The [OttoStash maintainer design record at a pinned commit](https://github.com/potto007/OttoStash/blob/746baeba2dfcb8a020d021393dd92d74f537a5e1/docs/decisions/0002-absorb-azucraftyboxes-crafting.md) describes separate menu, requirement and consumption surfaces and historical findings involving excluded resources and failed/single-ingredient consumption. These are attributed historical maintainer findings about the implementation discussed there, not claims of current bugs in another mod. They reinforce the need for one coherent eligibility/payment policy rather than only adjusting visible counts.

[Jotunn's RPC documentation](https://valheim-modding.github.io/Jotunn/tutorials/rpcs.html) requires custom RPC registration on server and clients. Adding a server-only custom message does not supply an unmodified client's missing handler. No Jotunn dependency is added.

## Server-only feasibility conclusion

Observed: recipe availability, UI permission to craft, item creation and payment in the inspected normal workflow depend on client-local player/inventory methods. Container inventories have separate authority, serialized state and ownership handover. A server-only Harmony patch cannot directly replace code executing on an unmodified client.

Inference: transparent direct crafting from chest-held ingredients with unmodified clients is not supported by the inspected paths. No working server-only transport or transaction mechanism has been established. Loading chests or counting their items would not resolve the client requirement gate. Shared configuration or server installation alone does not establish server-only feasibility.

A companion client module is outside the approved scope. Any further feasibility research must establish a supported mechanism for unmodified clients before implementation can proceed. Automatically restocking a player's inventory would be a different behavior with its own capacity, permission and duplication issues, not an equivalent implementation of direct chest crafting. These alternatives are outside the user-approved scope and will not be implemented.

## Proposed gameplay boundaries and open decisions

The candidate boundary is ordinary crafting at a valid native station using recipes already known to the player. Vanilla station level/usability, resource cost, quality, world modifiers, DLC, inventory capacity and result identity must remain valid. Building, fuel/ore feeding, repairs, upgrades, special upgrader materials and batch/skill-bonus outcomes need explicit scope decisions before inclusion; no broad Player requirement hook is authorized.

| Decision still open | Consequence to define before gameplay work |
| --- | --- |
| Supported server-only mechanism | Establish a native path satisfying unchanged-client UI/requirements and safe resource payment; companion clients are not permitted. |
| Radius and distance reference | Player versus station center; 3D versus horizontal distance, vertical separation, obstacles, loaded-sector boundaries and movement during crafting. No range default is selected. |
| Container kinds | Ordinary/reinforced/personal chests, carts/ships, world-generated containers, graves and modded containers. No allowlist is adopted. |
| Access rules | Whose resources may be used; private/group behavior, overlapping wards, creator/permission changes and server-admin exceptions. No permissive policy is chosen. |
| Source order | Personal inventory versus chests, nearest versus deterministic order, reserve/leave-one behavior and mixed ingredient qualities. |
| Concurrent access | Whether in-use chests are skipped, locked or coordinated, and how normal deposit/withdraw/open operations interact with reservations. |
| Unavailable resources | Missing, unloaded, stale, denied or insufficient sources; cancellation and failure messages. No resources may disappear on an unsuccessful craft. |
| Operation types | Upgrades, batches, single-ingredient alternatives, quality/custom metadata, skill-dependent extra output and full inventory handling. |
| Disconnection and persistence | What constitutes commitment, acknowledgement, timeout and recovery across player-local and container saves. |

Permission evaluation must bind the requesting session to the intended player identity, not assume a caller-supplied ID or transient container owner grants resource access. Range and permissions need re-evaluation at commitment, not only during discovery. Unknown or unavailable permission/ownership state must not authorize a withdrawal. These are design requirements, not implemented checks.

## Transaction and fallback requirements

A future design must identify one coherent request lifecycle: authenticated intent, current recipe/cost validation, deterministic source selection, conflict-safe reservation, current permissions/ownership/revision checks, result capacity, payment, result creation, commit acknowledgement and recovery. Multi-container and player-inventory operations span separate ownership and persistence domains; a server lock alone does not serialize vanilla clients' independent writes.

Two players seeing the same stock must not both spend it. Retry/disconnect/duplicate messages must not create another result or spend resources twice. A snapshot rollback must not overwrite legitimate intervening deposits or withdrawals. Resource and result accounting must retain native item identity and quantities rather than reconstruct approximate stacks. No transaction, idempotency, locking or rollback implementation exists for this research-only idea.

When nearby resources are unavailable or the integration cannot safely proceed, preserve normal vanilla crafting with resources already held by the player. A future fallback must occur before any partial withdrawal or result creation; calling vanilla after a partially applied custom payment risks double consumption or free output. Do not enable no-cost/world recipe cheats, manufacture ingredients or silently alter permissions to bypass the client feasibility blocker.

## Integration boundary

No NearbyChestCrafting source files, module registration, toggle, gameplay hooks or diagnostics are present. Any later implementation must first establish a supported server-only path under the user's restriction and receive authorization for the remaining consequential design choices. Normal module, configuration and suppression-policy responsibilities in [architecture](../architecture.md) and [contributing](../contributing.md) would apply only after that prerequisite is met. The [user-facing scope document](../features/nearby-chest-crafting.md) states that the capability is unavailable. Testing instructions and results remain in chat.
