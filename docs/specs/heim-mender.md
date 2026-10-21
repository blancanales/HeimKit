# HeimMender

HeimMender repairs damaged player-built static structures through native `WearNTear.RPC_Repair`. It does not prevent damage, claim ruins, rebuild destroyed pieces or change support. See [supported environment](../server-setup.md#supported-environment) and [shared limitations](../limitations.md).

## Behavior and native evidence

Eligible persistent prefabs have Piece, static WearNTear, a nonzero saved creator and BuildingWorkbench, BuildingStonecutter or DeepNorth category. Doors, walls, floors, roofs and structural beams qualify, including weather-immune materials. Containers, vehicles, moving rigidbodies, creatures, furniture, crafting stations, world-created pieces, dead pieces and invalid records are excluded. Maximum health uses native world-level scaling. Missing saved health means full health, not damage.

Installed WearNTear.Repair sends an owner-targeted RPC_Repair; its owner restores maximum health and broadcasts RPC_HealthChanged. HeimMender uses that owner path without ownership changes or creating world instances. Remote owners must be ready peers; a server owner must have an existing valid native WearNTear instance. Observed ownership changes, destruction or timeout leave an unconfirmed request, never a claimed repair. Confirmation requires the same ZDO identity and observed full health. The source creator and support remain untouched. Native RPC repair has no material cost.

The comparable aedenthorn BuildingRepair source selects colliders around Player.m_localPlayer and invokes WearNTear.Repair. That client-local implementation informs repair semantics; dedicated-server operation instead requires authenticated server observations and routing to the actual owner.

## Settings and defaults

All settings are in `[HeimMender]`. Enabled=true enables the requested service; DebugMode=false keeps routine output concise. AutomaticEnabled=true enables gradual restoration. IntervalSeconds=300 gives five-minute cycles; RepairChancePercent=50 prevents every damaged piece being repaired immediately; MaxRepairsPerCycle=10 caps requests as well as successful repairs. ScanRangeMeters=64 restricts work to a rotating joined player's surroundings. DamageQuietSeconds=60 postpones automatically repairing pieces with recently observed health loss. HostileClearanceMeters=30 postpones automatic work near recorded living untamed MonsterAI creatures; neutral members of that class can also postpone work. Admin requests bypass chance and these two automatic deferrals.

AdminRadiusMeters=64 and AdminMaxPieces=200 bound each manual request. RepairRequestId=0 is a monotonic operator trigger: increase it while running to request a batch at RepairTarget, an exact joined player name or peer UID. Lower request IDs are ignored until the running high-water mark is exceeded. An empty target is accepted only with exactly one ready joined player. Startup establishes the trigger baseline and never replays a saved request. Requests with no valid target fail immediately. No offline repair queue exists.

The feature-owned configuration observer watches the loader's existing heimkit.cfg; callbacks only mark changes. A main-thread, debounced, size-limited read stages and validates the complete HeimMender section before changing any bound value. A timestamp poll covers missed notifications. Missing settings retain their current values; invalid settings reject the whole section. Only this section reloads. Saving is suppressed during application to preserve other sections and external edits. A runtime module failure requires restart; changing Enabled cannot conceal it.

## Administration

Dedicated-server terminal command `heimmender repair <peerUID-or-single-word-name> [radius]` queues a nearby batch; quoted/multiword names use RepairTarget configuration instead. `heimmender status` reports queue state. Local terminal access is trusted server administration. Vanilla clients do not register this new console command. Authenticated server-routed normal chat `!heimmender repair` also requests a batch around its sender; the physical connection must match native admin-list authorization. Chat delivery follows native routing, described in shared limitations. Administrative limits still apply. One batch at a time; concurrent requests are rejected explicitly.

## Persistence and performance

World health persists through normal world saves. Observation history, random decisions and batches are transient; disconnect/pause/world replacement cancels pending work. There is no independent repair ledger or duplicate replay. Repeated native repair requests cannot create items, but are avoided with one in-flight request per ZDO.

Scans run every two seconds around one rotating player, reuse Plugin's player census, and retain at most 4096 damaged-piece observations and 256 nearby threat positions. Native sector queries can temporarily return more records; oversized scopes are rejected. Fully healthy pieces are not retained, and observed completed repairs release their candidate slots. First observation of damage starts the quiet period. Stale observations expire after 180 seconds. Warn once on damaged-candidate capacity exhaustion and announce recovery after a later scan has no omissions; do not raise the cap to accommodate healthy buildings. A cycle takes one random decision per eligible observed piece and queues no more than the configured cap. Up to four native requests are issued per scan, with ten-second confirmation timeouts. Administrative batches retain at most 2000 candidates and issue at most AdminMaxPieces requests. No full-world or per-frame object scan occurs. Unavailable observations retain native behavior.

## Player-service integration

The module receives Plugin-owned PlayerServices, subscribes to Events with replay, and uses Presence and shared RoutedMessages. It acquires no AFK activity lease. Native readiness requires PlayerServices.Ready, ZDOMan, ZNetScene and ZRoutedRpc. False or unknown presence disposes its controller, routed subscription and batches before pause acknowledgment. World replacement recreates the controller. Shutdown is idempotent. The configuration watcher and trusted local command registration are explicit access-management/operator-request exceptions, separate from gameplay; they may remain installed while empty, but cannot scan or repair without a ready joined player. Enabled=false releases gameplay; configuration monitoring remains available for reactivation. Compatibility failure never activates gameplay.

## Logging and acceptance contract

Normal output announces lifecycle, accepted/rejected admin requests and one confirmed repair count at the completed automatic/manual batch boundary. Requests and timeouts are not counted as repairs. Debug summaries own bounded cycle counts, examined scope, damaged/quiet/threat/owner exclusions, queue and confirmed/unconfirmed counts. Object samples carry ZDO ID, prefab, health and owner, limited per minute. Immediate runtime failures disable only HeimMender. Empty, unchanged scopes stay quiet. Acceptance requires independent operation with other modules disabled, bounded requests, preserved creator/support, authenticated administration, observed confirmation, atomic section reload, no work while empty and no restored destroyed or world-created pieces.

Optional controller-pass timing reports duration_avg_ms and duration_max_ms at most once per minute for nonempty scope. Collection follows effective debug policy and includes synchronous controller processing only; see [shared measurement limitations](../limitations.md).
