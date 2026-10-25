# SeaSalvage specification

## Behavior and native evidence

Recover inaccessible sunken loose ItemDrop records into newly created vanilla CargoCrate containers. Installed CargoCrate has persistent ZNetView, Floating (0.5 metre water offset), a public 2x2 Container, no default loot and native empty-container destruction. Its inventory and float behavior require no client handlers. The [LetItFloat reference](https://github.com/ArgusMagnus/ValheimServersideQoL/blob/main/ServersideQoL.LetItFloat/ItemDropProcessor.cs) uses this prefab; HeimKit keeps fixed native capacity and does not merge stacks or mutate client component fields.

Eligible definitions have ItemDrop and persistent ZNetView, no Floating, Fish, Character, Piece, Container or TombStone. Require explicit serialized native itemData, valid positive stack no larger than its native stack limit, finite durability and supported native item serialization. Placed/world pickups, tombstones, ships, creatures, fish, already floating drops, legacy/noncurrent serialized item formats, unsupported metadata and interiors are excluded. Quality, quantity, durability, variant, crafter and custom data remain native through validated ItemData decoding and the installed Inventory format, one complete drop per slot. Copy current native payload bytes and change only the inventory grid coordinates; preserve the original integer durability without a float re-quantization. Require a matching serialized prefab hash and no trailing payload.

## Configuration and defaults

SeaSalvage.Enabled=true enables eligible sunken-drop recovery by default; DebugMode=false. MinimumDepthMeters=4 (3..100), MaximumDepthMeters=50 (4..500), DelaySeconds=30 (10..3600), ScanRangeMeters=128 (32..512), GroupRadiusMeters=4 (0..10), SlotsPerCrate=4 (1..4), MaxItemsPerPass=16 (1..32), IntervalSeconds=2 (1..30). The depth interval must be ordered. These defaults avoid shallow collectible drops, wait for settling and keep native crate capacity. No player may be within ten metres in 3D, including the latest observed movement segment. Scan one ready actor per pass; require all current actor observations to be reliable for pickup exclusion. Probe at most four settled candidates per pass using a rotating cursor. Track at most 4,096 settling observations and reject over-8,192-record sector scopes. Owner, revision or position changes restart settling.

## Ownership and transfer

Only stable unassigned or already server-owned sources qualify. Active client-owned drops wait for native release; HeimKit does not steal their ownership or send a pickup request. Immediately before transfer, revalidate owner, item bytes, position and player clearance. Retain server authority with shared incoming-data/destruction fencing until native destruction completes. Missing water/ground context defers processing. Use confirmed native water liquid level, exclude lava and interiors, and require terrain generation/context before creating a surface crate.

Group at most the configured whole-drop slot count into a new crate within GroupRadiusMeters of its first source's horizontal position. Never reopen or append to an existing published crate, avoiding container-open and inventory ownership races. Zero grouping radius means one source per crate. Stage native inventory-format bytes before source mutation. On the main thread create the crate, persist its full native inventory, then write a source consumed marker and zero native item data before queuing source destruction. An exception before commit rolls back the source and removes the unpublished crate. Native save snapshots cannot interleave this synchronous transaction; saved consumed/zero sources cannot be salvaged twice even if queued destruction was not flushed. On restart zero/consumed sources are omitted. Old incoming source updates are rejected while retained; native dead-ID handling follows deletion. Native world saves own cargo and consumed source state, without a separate cross-file inventory journal.

Crates are released immediately to native simulation/ownership after publication. They can drift, be opened, be destroyed and drop contents normally. The module retains no ownership of published cargo. Normal output reports item-drop count transferred per completed processing pass; it does not claim player pickup or client delivery.

Shared terrain preparation caches terrain-related prefab classification and reuses native scene inclusion buffers across probes. Shared authority hooks skip packet processing while no consumer retains records; consumed sources remain fenced until native destruction completes. The smaller default scan range applies to new or missing settings; existing configured ranges are preserved.

Loose-drop discovery uses the shared spatial candidate index, seeded on activation and entry into another player sector. Successful replicated/local changes update it, including ownership release and movement of shallow drops. Client-owned and shallow drops remain indexed. Once seeded, an unchanged area without candidates performs no repeated broad discovery or terrain query. Settling, depth, native item validation and transfer fencing retain their existing cadence. Full terrain context is queried only for a candidate reaching the terrain stage.

## Player-service integration

Follow the [required feature hookup](../architecture.md#required-feature-hookup). Plugin injects its existing PlayerServices. The module owns one presence subscription and at most one controller per world. Enablement, exact game compatibility, dedicated-server world readiness and joined-world presence gate gameplay independently. False or unknown presence disposes the controller before acknowledging pause; reconnect creates a fresh controller. World replacement disposes old resources. No activity lease or AFK inference is needed. Ready is reported once independently of presence. There is no independent empty-server gameplay responsibility.

## Logging

Use the [shared logging and suppression policy](../architecture.md#logging). Enabled/Disabled, Ready and failures remain visible independently of DebugMode. Each completed processing pass combines confirmed results into one Info count; empty passes are silent. Optional changed-result diagnostics have a single summary per 60 seconds, with pass and lifetime counts. A separate at-most-once-per-minute eligibility summary reports client-owned, player-near, settling, terrain, depth and invalid-item counts. Sample at most 16 completed transfers per minute with original source and cargo IDs. Pending state clears on controller disposal, with native world persistence retaining published cargo and consumed source data. The effective global-or-feature policy and hidden suppression apply before optional formatting.

Optional controller-pass timing reports duration_avg_ms and duration_max_ms at most once per minute for nonempty scope. Collection follows effective debug policy and includes synchronous controller processing only; see [shared measurement limitations](../limitations.md).

## Acceptance criteria

Each accepted source contributes its complete quantity to exactly one native inventory slot, preserving serialized durability, quality and metadata. Full crates split groups without truncation. Active client ownership or pickup-clearance uncertainty prevents transfer. A source is queued for destruction only after cargo inventory and its consumed state are recorded; synchronous rollback empties unpublished cargo before restoring sources. Restart recovery never processes a consumed source again.

## Compatibility and shared criteria

Use the [supported environment](../server-setup.md#supported-environment) and shared exact-version guard. Each module isolates failure and releases only its own resources. Disabled features bind configuration but install no gameplay hooks and scan nothing. Objects outside verified scope retain native behavior. Acceptance requires independent enablement, repeatable cleanup, native state preservation, bounded retained state and the ownership/persistence rules above. Shared observation and delivery qualifications belong in [limitations](../limitations.md#new-world-features).

## HeimKit Core integration

The performance rebuild in `src/HeimKit/Features/` consumes CandidateSubscription for loose drops and terrain context, PrefabRegistry item/crate metadata, TerrainContext, NativeWorldObject, ObjectAuthority/ZdoPackets and ModuleScheduler. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.
