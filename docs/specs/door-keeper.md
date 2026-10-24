# DoorKeeper specification

## Behavior and native evidence

Close eligible player-built doors after a continuous observed open interval; never automatically open. Installed Door uses state 0 for closed and +/-1 for open. RPC_UseDoor toggles and CanInteract checks animation tags and excludes keyed/non-closing doors. Automatic closure runs locally through that native handler only after rechecking a nonzero state and CanInteract on the same main-thread call. Manual interactions retain native access, ward and animation checks; no key or ward permissions are granted.

Supported exact prefabs: wood_door, wood_gate, wood_fence_gate, darkwood_gate, iron_grate, stave_gate, piece_dvergr_wood_door, piece_hexagonal_door, flametal_gate and ashwood_door. Each requires a persistent native ZNetView, Door, Piece, nonzero saved creator, no key, no cannot-close flag and ordinary open/closed semantics. Missing or changed definitions are omitted. Drawbridges, cloth curtains, world/dungeon doors, keyed doors, inverted controls and unsupported objects are excluded. The [AutoDoors reference](https://github.com/ArgusMagnus/ValheimServersideQoL/blob/main/ServersideQoL.AutoDoors/DoorProcessor.cs) demonstrates direct state processing but does not establish owner-race safety.

## Configuration and defaults

All settings are in DoorKeeper and require restart. Enabled=true provides automatic door closing by default; DebugMode=false follows the shared policy. CloseDelaySeconds=5 (1..300) allows normal passage. ClearanceMeters=3 (2..10) is the minimum player exclusion sphere radius around the native collision-bound center. Automatically enlarge it to the scaled collision-bound half-diagonal plus a one-metre player-body allowance, covering wide and tall gates. Unknown geometry/scale defers closure. ScanRangeMeters=64 (16..128) limits work near a rotating ready player. IntervalSeconds=2 (0.5..10) bounds polling. All ready player positions must be usable; otherwise automatic closure pauses. Both observed current positions and the previous processing observation's movement segment must clear the sphere. Fast or missing replication remains a shared observation limitation.

## Ownership, persistence and performance

The controller tracks at most 1,024 open doors without taking ownership during their delay or occupancy wait. Rotate at most one due, clear door's context preparation per pass. Prepare terrain at that door and include required collision/structure records within a conservative sixteen-metre horizontal neighbourhood enlarged by the door's and each surrounding record's scaled collision bounds. Retain vertical roof context and unknown geometry rather than treating it as absent; exclude character and loose-drop instances. Retain at most 8,192 context records, subject to native scene creation budgets, and defer until required instances exist. After validating the native door instance and animation state, take explicit server authority only around the synchronous native close call and release it in a finally block. No door stays owned between passes; closed-state observation removes its timer. Shared authority fencing protects retained records while held. No private state survives pause/restart: an already open door starts a new full delay. Native door state remains in the world save. Cached typed delegates invoke the native interaction methods.

Shared scanning rotates one of at most 64 usable ready actors per pass and rejects an over-8,192-record sector scope instead of truncating safety evidence. Manual closed-state observation clears the timer. Occupancy postpones closure without an opening request. Native confirmed state transitions are grouped per pass as closed-door counts.

## Terrain readiness

Shared terrain preparation checks the heightmap, paint mask, pending rebuilds and native compiler initialization before invoking saved-terrain loading. A compiler whose native Awake returned early for a missing heightmap may resume that initialization only once a usable heightmap exists. Missing/empty compiler arrays or a mismatched heightmap postpone closure rather than calling Load with incomplete state. Unexpected native failures still follow module failure isolation. Debug eligibility summaries include the terrain wait reason and blocking object when available.

## Player-service integration

Follow the [required feature hookup](../architecture.md#required-feature-hookup). Plugin injects its existing PlayerServices. The module owns one presence subscription and at most one controller per world. Enablement, exact game compatibility, dedicated-server world readiness and joined-world presence gate gameplay independently. False or unknown presence disposes the controller before acknowledging pause; reconnect creates a fresh controller. World replacement disposes old resources. No activity lease or AFK inference is needed. Ready is reported once independently of presence. There is no independent empty-server gameplay responsibility.

## Logging

Use the [shared logging and suppression policy](../architecture.md#logging). Enabled/Disabled, Ready and failures remain visible independently of DebugMode. Each completed processing pass combines confirmed results into one Info count; empty passes are silent. Optional changed-result summaries have a single report per 60 seconds, with pass/lifetime counts. A separate changed eligibility summary, also at most once per minute, gives context readiness, delay waits and occupied counts; no individual object stream. Pending state clears on controller disposal, except the durable ForestRenewal ledger. The effective global-or-feature policy and hidden suppression apply before optional formatting.

## Acceptance criteria

A qualifying observed open interval may produce only a close transition, never an open transition. Occupied or unknown doorway state postpones closure. Manual native close resets the delay, keyed/world objects remain untouched, and controller disposal relinquishes every retained door and context inclusion.

## Compatibility and shared criteria

Use the [supported environment](../server-setup.md#supported-environment) and shared exact-version guard. Each module isolates failure and releases only its own resources. Disabled features bind configuration but install no gameplay hooks and scan nothing. Objects outside verified scope retain native behavior. Acceptance requires independent enablement, repeatable cleanup, native state preservation, bounded retained state and the ownership/persistence rules below. Shared observation and delivery qualifications belong in [limitations](../limitations.md#new-world-features).

Static retained-door positions use a spatial cell index for native active-area queries rather than comparing every retained door with every wear update. Shared context expires after ten seconds without refresh even if no further operation runs; idle scene hooks avoid allocating inclusion sets.

Optional controller-pass timing reports duration_avg_ms and duration_max_ms at most once per minute for nonempty scope. Collection follows effective debug policy and includes synchronous controller processing only; see [shared measurement limitations](../limitations.md).

Door discovery uses the shared spatial candidate index with activation/area-entry seeding and replicated/local change intake. Indexed doors still have live state, creator, delay and clearance checked at the configured interval. Full surrounding safety context is queried only for the selected due, clear door; native synchronous closing and immediate authority release remain unchanged.

## HeimKit Core integration

The performance rebuild in `src/HeimKit2/Features/` consumes CandidateSubscription for doors and context, PrefabRegistry door/collision metadata, NativeAccess delegates, TerrainContext, ObjectAuthority and ModuleScheduler. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.
