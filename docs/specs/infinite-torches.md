# Infinite Torches specification

## Purpose and scope

Infinite Torches periodically refills eligible torches to their ordinary maximum fuel on a Windows Valheim dedicated server, without spending player inventory materials. It uses existing vanilla networking and is designed for unmodified clients. Environmental conditions can still extinguish a fueled torch.

Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

## Configuration

| Section | Setting | Default | Rules |
| --- | --- | --- | --- |
| Torches | Enabled | true | Independent control; restart after editing. |
| Torches | IntervalSeconds | 5 | Range 2–60 seconds. |
| Torches | MaxMessagesPerPass | 32 | Range 1–256 requests. |
| Torches | Prefabs | Listed below | Comma-separated candidates, checked at runtime. |
| Diagnostics | DebugMode | false | Shared troubleshooting switch. |

Default prefabs are `piece_groundtorch_wood`, `piece_groundtorch`, `piece_groundtorch_green`, `piece_groundtorch_blue` and `piece_walltorch`.

HeimKit creates `heimkit.cfg` in BepInEx's configuration directory, preserving existing values and adding missing defaults. The example config is optional. Retired diagnostic category settings are removed without changing DebugMode.

## Eligibility and processing

The module waits for dedicated-server networking and world services. Each configured prefab must have a refillable Fireplace, a finite positive maximum fuel, no inherent infinite-fuel flag, no toggle-off behavior and no wet low/high flame objects. Missing or unsupported definitions are warned about and skipped.

Each pass scans ready connected players' nearby processing sectors. Valid candidate records must be owned by the corresponding peer. Duplicate records are suppressed across players. Off-state torches and nonfinite fuel values are skipped.

Below 75% of normal capacity, the server sends vanilla `RPC_SetFuelAmount(float)` to the current network owner, targeting the normal maximum. The module does not claim ownership or directly overwrite remote-owned fuel data. A rotating cursor and per-pass message budget distribute requests across candidates.

## Pending requests

Pending requests are tracked by ZDOID, with a maximum of 2,048 entries. A tracked object receives no additional request until reconciliation removes the entry. All pending records are reconciled, including those outside current scan areas.

Tracking clears when the object disappears, its owner changes, sufficient replicated fuel progress is observed or 30 seconds pass without sufficient progress. Progress requires finite fuel above the prior value by more than 0.001 and at least 75% of the requested target. Capacity exhaustion defers new requests. Routing exceptions remove the corresponding reservation before module failure handling.

Fuel observations are not delivery acknowledgements or proof that HeimKit caused the increase. Late delivery can overlap a timeout retry; exactly-once delivery is not guaranteed.

## Lifecycle and failures

The module initializes only when enabled and the audited game-version guard passes. Disabled modules perform no world work. Runtime errors identify the operation and disable this module independently. Initialization and shutdown clear caches, pending records and scheduling state; shutdown is repeatable.

## Logging

Standard output includes configuration path, startup state, world/prefab readiness, eligibility warnings and errors. DebugMode adds per-pass request counts, skip/pending/deferred counts and reconciliation outcomes.

Status reports are limited to one per 60 seconds. Actual refill-request details are limited to four samples per 60 seconds across all passes. Suppressed detail messages are not formatted. Samples identify object, prefab, owner, position, prior fuel, target and RPC, and state that client application is unconfirmed. Counts describe examined candidates in the current pass, not every torch in the world.

## Limits

Unloaded objects and ownership transitions may ignore requests; later passes retry. Newly loaded empty torches may briefly remain dark. Normal refill effects can appear, and a simultaneous manual refill can still consume resin. Smoke, obstruction and water behavior remains vanilla. The message budget limits sends, not sector scanning cost.

Removing the plugin leaves ordinary fuel values to burn normally. No custom world objects or client RPC handlers are introduced.

## Source responsibilities

| File in `src/HeimKit/Features/InfiniteTorches/` | Responsibility |
| --- | --- |
| InfiniteTorches.cs | Settings construction, compatibility, module lifecycle and failure isolation. |
| InfiniteTorchesSettings.cs | Feature configuration bindings, defaults and validation ranges. |
| InfiniteTorchesController.cs | World readiness, scheduling, scanning, selection and native refill requests. |
| InfiniteTorchesPendingRefills.cs | Duplicate suppression, reconciliation, timeout and capacity control. |
| InfiniteTorchesDiagnostics.cs | Bounded diagnostic output using the shared debug switch. |
| Models/RefillObservation.cs | Observed owner and fuel data for reconciliation. |
| Models/RefillRequest.cs | Pending request owner, fuel target and timing data. |

User installation and general configuration belong in [server setup](../server-setup.md); feature usage and troubleshooting belong in the [feature guide](../features/infinite-torches.md). Project design belongs in [architecture.md](../architecture.md); file placement and development procedures belong in [contributing.md](../contributing.md).

ConfigurationLoader manages the shared file, and DiagnosticsSettings binds the common debug switch. The main class activates the controller only after enable and compatibility checks pass; the controller performs gameplay work and reports processing failures to the main class.
