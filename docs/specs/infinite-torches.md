# InfiniteTorches specification

Gameplay and observation limitations: [safety and limitations](../limitations.md).
## Purpose and scope

The feature display name and log prefix are exactly `InfiniteTorches`. Its existing configuration section remains `[Torches]`.

InfiniteTorches periodically refills eligible torches, braziers, campfires, hearths and bonfires to their ordinary maximum fuel on a Windows Valheim dedicated server, without spending player inventory materials (including brazier coal). It uses existing vanilla networking and is designed for unmodified clients.

Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

## Configuration

Debug settings use the [shared suppression contract](../server-setup.md#hidden-per-feature-debug-suppression).

| Section | Setting | Default | Rules |
| --- | --- | --- | --- |
| Torches | Enabled | true | Independent control; restart after editing. |
| Torches | IntervalSeconds | 5 | Range 2–60 seconds. |
| Torches | MaxMessagesPerPass | 32 | Range 1–256 requests. |
| Torches | IncludeFireplaces | false | Master switch for campfires, hearths and bonfires; false excludes all three even if listed manually. |
| Torches | Campfires | true | With IncludeFireplaces enabled, refill `fire_pit`. |
| Torches | Hearths | true | With IncludeFireplaces enabled, refill `hearth`. |
| Torches | Bonfires | true | With IncludeFireplaces enabled, refill `bonfire`. |
| Torches | Prefabs | Listed below | Comma-separated candidates, checked at runtime. |
| Torches | DebugMode | false | Enable this feature’s diagnostics with global debug off; hidden suppression takes precedence. |
| Diagnostics | DebugMode | false | Enable diagnostics across features unless suppressed. |

Default prefabs are `piece_groundtorch_wood`, `piece_groundtorch`, `piece_groundtorch_green`, `piece_groundtorch_blue`, `piece_walltorch`, `piece_brazierceiling01`, `piece_brazierfloor01` and `piece_brazierfloor02`. The brazier entries cover the hanging brazier and both standing variants. Existing Torches.Prefabs values are preserved; upgrades require adding the three brazier entries to an existing list.

HeimKit creates `heimkit.cfg` in BepInEx's configuration directory, preserving existing values and adding missing defaults. The example config is optional. Retired diagnostic category settings are removed without changing DebugMode.

## Eligibility and processing

The module waits for dedicated-server networking and world services. Each configured prefab must have a refillable Fireplace, a finite positive maximum fuel, no inherent infinite-fuel flag, no toggle-off behavior and no wet low/high flame objects, except for the three exact default brazier prefabs and the three exact fireplace prefabs when IncludeFireplaces and their respective per-type switch are enabled. Their native wet-flame behavior remains active. Missing or unsupported definitions are warned about and skipped.

Each pass scans ready connected players' nearby processing sectors. Valid candidate records must be owned by the corresponding peer. Duplicate records are suppressed across players. Off-state torches and nonfinite fuel values are skipped.

Below 75% of normal capacity, the server sends vanilla `RPC_SetFuelAmount(float)` to the current network owner, targeting the normal maximum. The module does not claim ownership or directly overwrite remote-owned fuel data. A rotating cursor and per-pass message budget distribute requests across candidates.

The native Fireplace RPC_SetFuelAmount handler changes only fuel, plays the normal fuel-added effects and refreshes native state. It does not change the on/off state, claim ownership, consume inventory or disable environmental checks. Braziers use the same owner-routed request, capacity threshold, pending tracking and player-presence lifecycle as torches. IncludeFireplaces defaults false. Enabling it adds only those fire types whose Campfires, Hearths or Bonfires switch is true, without rewriting existing Prefabs values. The three type switches default true and have no effect while IncludeFireplaces is false. A disabled type cannot bypass its switch through Prefabs. These fires use the same native fuel request, request budget, pending reconciliation and occupancy lifecycle. Their native warmth, cooking, smoke, shelter and water behavior is unchanged. World-prop braziers remain outside the default candidates.

## Pending requests

Pending requests are tracked by ZDOID, with a maximum of 2,048 entries. A tracked object receives no additional request until reconciliation removes the entry. All pending records are reconciled, including those outside current scan areas.

Tracking clears when the object disappears, its owner changes, sufficient replicated fuel progress is observed or 30 seconds pass without sufficient progress. Progress requires finite fuel above the prior value by more than 0.001 and at least 75% of the requested target. Capacity exhaustion defers new requests. Routing exceptions remove the corresponding reservation before module failure handling.

Timeout retries follow the pending-record rules; [delivery limits](../limitations.md#weatherguard-and-infinitetorches) are centralized.

## Lifecycle and failures

The module initializes only when enabled and the audited game-version guard passes. Disabled modules perform no world work. Runtime errors identify the operation and disable this module independently. Initialization and shutdown clear caches, pending records and scheduling state; shutdown is repeatable.

## Logging

All messages follow the [shared logging contract](../architecture.md#logging). Info uses concise configuration, effective startup state and readiness messages; Warning uses concise problem messages. Settings and prefab-processing details belong in Debug; Error and exception evidence retain structured output. Optional diagnostics use BepInEx Debug severity. The shared [repetition and volume limits](../architecture.md#repetition-and-volume-limits) govern all streams. DebugMode permits at most one changed-state scope summary per 60 seconds with explicitly labeled latest-pass/current counts, interval refill-request and reconciliation-outcome counts and omitted-message counts.

Each reconciliation pass with sufficient replicated fuel increases emits one concise Info entry: `InfiniteTorches: Fuel increase observed for N light sources.` A single update uses singular wording. All observed updates in that pass share the entry, independently of DebugMode and optional sample limits. The pending tracker returns the observed-update count after clearing completed requests, preventing repeated notices for unchanged fuel. Empty passes, requests merely sent, insufficient increases, timeouts, missing objects and owner changes do not produce a successful-update summary. The count covers pending requests confirmed by the existing sufficient-progress criteria, not all world light sources. Responses observed in different passes have separate summaries; results follow the shared log interpretation rules. Pause/shutdown clears pending state.

Refill request sample records are limited to four per 60 seconds. They identify object, prefab, position, prior fuel and target; they identify the sent native request. Pending-operation outcome sample records are limited to six per 60 seconds. They identify observed fuel increase, timeout, owner change or missing object, with prior/target/observed fuel and elapsed wait. An owner-change result includes the owners needed to explain that transition. Unknown observed data is omitted from HeimKit fields.

Every reconciliation outcome is counted by the controller, even when its individual outcome sample message is omitted. There is no unbounded per-pass request message or direct pending-refill logging bypass. Scope summary sentences identify the latest examined pass and the activity window since the preceding emitted summary; current counts describe the examined scope.

## Limits

Request budgets bound sends rather than sector enumeration. Removing the plugin leaves ordinary fuel values; no custom objects or client handlers are introduced. Gameplay/observation caveats are centralized in [limitations](../limitations.md#weatherguard-and-infinitetorches).

## Source responsibilities

| File in `src/HeimKit/Features/InfiniteTorches/` | Responsibility |
| --- | --- |
| InfiniteTorches.cs | Settings construction, compatibility, module lifecycle and failure isolation. |
| InfiniteTorchesSettings.cs | Feature configuration bindings, defaults and validation ranges. |
| InfiniteTorchesController.cs | World readiness, scheduling, scanning, selection and native refill requests. |
| InfiniteTorchesRefillRules.cs | Prefab fuel eligibility and the exact brazier and configurable fireplace wet-flame exceptions. |
| InfiniteTorchesPendingRefills.cs | Duplicate suppression, reconciliation, timeout and capacity control. |
| InfiniteTorchesDiagnostics.cs | Bounded diagnostic output using the global-or-feature debug policy. |
| InfiniteTorchesLogTopics.cs | Feature log topic. |
| Models/RefillObservation.cs | Observed owner and fuel data for reconciliation. |
| Models/RefillRequest.cs | Pending request owner, fuel target and timing data. |
| Models/RefillResult.cs | Reconciled request outcome, observed state and elapsed wait. |

## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into InfiniteTorches. The enabled module subscribes once to occupancy and releases its controller/pending-request state when empty or unavailable. Occupancy recreates the controller, retaining native world/prefab readiness checks and the configured refill interval. Ready-owner selection uses the shared presence snapshot. There is no activity lease because fuel eligibility does not depend on inferred idle. Shutdown or failure removes the presence registration; other consumers remain independent. The module sends no offline refill requests.

No empty-server exception applies; refill eligibility uses ready characters and native world/prefab readiness.

Prefab lookup combines all unsupported entries into one Warning containing the total and counts grouped by rejection reason, with Torches.Prefabs as the setting to review. Successful definitions still participate normally. If no supported definitions remain, the separate availability warning describes that distinct failure to become ready. Per-object diagnostics remain Debug; ordinary fuel-update batches keep their existing reconciliation boundary. See the [standard logging contract](../architecture.md#standard-feature-events-and-grouping).

## Game-version compatibility

Use the shared [compatibility contract](../architecture.md#configuration) before activation.

## HeimKit Core integration

The performance rebuild in `src/HeimKit/Features/` consumes near-only owner-filtered CandidateSubscription, PrefabRegistry fireplace metadata and ModuleScheduler refill cadence; no ownership lease. Discovery uses native ready-owner sector geometry, activation/area-entry seeding and local/replicated updates. Fuel, state and owner are checked live at the refill interval. Pending requests reconcile independently of discovery; ownership changes do not discard eligible light definitions. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.
