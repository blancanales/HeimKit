# InfiniteTorches specification

## Purpose and scope

The feature display name and log prefix are exactly `InfiniteTorches`. Its existing configuration section remains `[Torches]`.

InfiniteTorches periodically refills eligible torches to their ordinary maximum fuel on a Windows Valheim dedicated server, without spending player inventory materials. It uses existing vanilla networking and is designed for unmodified clients. Environmental conditions can still extinguish a fueled torch.

Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

## Configuration

Automatic Debug output follows the shared [five-minute empty-server pause](../architecture.md#empty-server-debug-pause). Reconnection resumes eligible diagnostics without restarting the feature; warnings, errors, requested reports and gameplay remain available.

The shared [hidden debug suppression contract](../server-setup.md#hidden-per-feature-debug-suppression) applies in this feature's existing configuration section. Settings resolve it through DebugLoggingResolver; effective debug policy gates optional output and diagnostic collection while gameplay and standard lifecycle/warnings/errors remain active. The key has no generated default.

| Section | Setting | Default | Rules |
| --- | --- | --- | --- |
| Torches | Enabled | true | Independent control; restart after editing. |
| Torches | IntervalSeconds | 5 | Range 2–60 seconds. |
| Torches | MaxMessagesPerPass | 32 | Range 1–256 requests. |
| Torches | Prefabs | Listed below | Comma-separated candidates, checked at runtime. |
| Torches | DebugMode | false | Enable this feature’s diagnostics with global debug off; hidden suppression takes precedence. |
| Diagnostics | DebugMode | false | Enable diagnostics across features unless suppressed. |

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

All messages follow the [shared logging contract](../architecture.md#logging). Info uses concise configuration, effective startup state and readiness messages; Warning uses concise problem messages. Settings and prefab-processing details belong in Debug; Error and exception evidence retain structured output. Optional diagnostics use BepInEx Debug severity. The shared [repetition and volume limits](../architecture.md#repetition-and-volume-limits) govern all streams. DebugMode permits at most one changed-state SUMMARY per 60 seconds with explicitly labeled latest-pass/current counts, interval refill-request and reconciliation-outcome counts and omitted-message counts.

Each reconciliation pass with sufficient replicated fuel increases emits one concise Info entry: `InfiniteTorches: Fuel increase observed for N torches.` A single update uses singular wording. All observed updates in that pass share the entry, independently of DebugMode and optional sample limits. The pending tracker returns the observed-update count after clearing completed requests, preventing repeated notices for unchanged fuel. Empty passes, requests merely sent, insufficient increases, timeouts, missing objects and owner changes do not produce a successful-update summary. The count covers pending requests confirmed by the existing sufficient-progress criteria, not all world torches. Responses observed in different passes have separate summaries; this remains evidence of replicated fuel progress rather than delivery acknowledgement or causation. Pause/shutdown clears pending state.

Refill ACTION records are limited to four per 60 seconds. They identify object, prefab, position, prior fuel and target; they report a request sent without asserting client application. Pending-operation RESULT records are limited to six per 60 seconds. They identify observed fuel increase, timeout, owner change or missing object, with prior/target/observed fuel and elapsed wait. An owner-change result includes the owners needed to explain that transition. Missing observed data is `unknown`.

Every reconciliation outcome is counted by the controller, even when its individual RESULT message is omitted. There is no unbounded per-pass request message or direct pending-refill logging bypass. A replicated fuel increase is not a delivery acknowledgement or proof of causation. SUMMARY counters labeled `_pass` refer to the latest examined pass, not every torch in the world; `_interval` counters cover activity since the preceding emitted summary.

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
| InfiniteTorchesDiagnostics.cs | Bounded diagnostic output using the global-or-feature debug policy. |
| Models/RefillObservation.cs | Observed owner and fuel data for reconciliation. |
| Models/RefillRequest.cs | Pending request owner, fuel target and timing data. |
| Models/RefillResult.cs | Reconciled request outcome, observed state and elapsed wait. |

User installation and general configuration belong in [server setup](../server-setup.md); feature usage and troubleshooting belong in the [feature guide](../features/infinite-torches.md). Project design belongs in [architecture.md](../architecture.md); file placement and development procedures belong in [contributing.md](../contributing.md).

ConfigurationLoader manages the shared file, and DiagnosticsSettings binds the common debug switch. The main class activates the controller only after enable and compatibility checks pass; the controller performs gameplay work and reports processing failures to the main class.

## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into InfiniteTorches. The enabled module subscribes once to occupancy and releases its controller/pending-request state when empty or unavailable. Occupancy recreates the controller, retaining native world/prefab readiness checks and the configured refill interval. Ready-owner selection uses the shared presence snapshot. There is no activity lease because fuel eligibility does not depend on inferred idle. Shutdown or failure removes the presence registration; other consumers remain independent. The module sends no offline refill requests.

No empty-server exception applies; refill eligibility uses ready characters and native world/prefab readiness.

Gameplay lifecycle uses the shared joined-world state, established by native character spawning rather than an incoming connection or login handshake. After its own cleanup completes, the module acknowledges an empty-server pause to the shared service. One shared PlayerPresence notice confirms all features are inactive until a player joins; individual empty-server notices are suppressed. After a player joins, the shared active notice waits for successful initialization of all enabled consumers. Each enabled module reports Ready once when its required dedicated-server services become available, regardless of players. This indicates readiness to process, not installed player-dependent hooks or a verified gameplay effect. Individual processing activation and pause reasons belong in Debug, including later starts; no per-feature Resumed notice appears in normal output. Death/respawn does not end a joined session. See the [shared lifecycle](../architecture.md#presence-driven-lifecycle).

Prefab lookup combines all unsupported entries into one Warning containing the total and counts grouped by rejection reason, with Torches.Prefabs as the setting to review. Successful definitions still participate normally. If no supported definitions remain, the separate availability warning describes that distinct failure to become ready. Per-object diagnostics remain Debug; ordinary fuel-update batches keep their existing reconciliation boundary. See the [standard logging contract](../architecture.md#standard-feature-events-and-grouping).

Debug policy startup declarations follow the [shared contract](../architecture.md#debug-policy-startup-declaration), identifying global/feature enablement and hidden suppression independently of gameplay enablement.

Detected loader Debug filtering is explained by a Warning followed immediately by Info instructions for the affected outputs; see [server configuration](../server-setup.md#configuration).
