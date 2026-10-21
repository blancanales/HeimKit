# ForestRenewal

Renews supported trees from either a stump left behind or its confirmed-cleared location. It finds existing/new stumps, waits, and has a configurable chance to replace the stump with the corresponding vanilla sapling. Removing a tracked stump keeps the same renewal timer and chance decision. Native biome, sunlight, spacing and growth rules remain.

Supported families are beech, birch, oak, fir, pine and the plantable large fir. Snow, ancient/swamp, Yggdrasil, Ashlands and decorative stumps are excluded. Exact tree/stump/sapling names belong in the [specification](../specs/forest-renewal.md).

Renewal checks for existing saplings and crops immediately before placement, regardless of who planted them. PlantExclusionMeters defaults to ten metres of horizontal clearance and can be increased up to fifty metres, separately from the building buffer. The larger native growth radius of the new sapling and existing plant also remains a minimum. Planting during a pending delay blocks renewal inside that clearance. A blocked renewal leaves both the existing plant and any source stump intact. DelaySeconds controls when HeimKit places a sapling; the sapling's subsequent growth time stays vanilla.

Defaults wait five minutes from first observation/removal, give each stump one 50% chance, avoid player structures within ten metres of their collision footprint, and avoid dirt, paved or cultivated ground within a three-metre square sampling radius. The building check runs just before renewal, including structures built during the delay. Walls, floors, beams and other player-built pieces all count; height separation does not bypass the horizontal buffer. Increase StructureExclusionMeters for a wider buffer. Set ChancePercent=100 if every otherwise eligible stump should renew. Existing old stumps start a full delay when first discovered. A persisted 24-hour native-world-time cooldown covers renewals within five horizontal metres. Native paint can identify cleared roads; unpainted paths cannot be classified.

Pending renewals and consumed decisions are stored beneath the active loader's config directory in `HeimKit/ForestRenewal/world-<world UID>.renewals`. Preserve this file and its backup with the world. Corrupt or incomplete ledger recovery stops this feature. Native world time controls pending delays. The at-most-once persistence policy and crash behavior are described in [limitations](../limitations.md#new-world-features).

## Configuration

Use `[ForestRenewal]` in `heimkit.cfg`. This feature defaults enabled; set Enabled=false to disable it. Stop the server before editing and restart to apply settings. HeimKit preserves existing values and supplies missing defaults.

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | true | Enable ForestRenewal. |
| DebugMode | false | Enable feature diagnostics through the shared debug policy. |
| DelaySeconds | 300 | Delay from first stump observation or confirmed removal. |
| ChancePercent | 50 | One renewal chance per original stump. |
| StructureExclusionMeters | 10 | Horizontal clearance from player-built piece footprints; zero disables it. |
| PlantExclusionMeters | 10 | Horizontal clearance from existing saplings/crops; configurable from 10 to 50 metres. |
| ExcludeClearedGround | true | Exclude dirt, cultivated and paved ground. |
| ExclusionRadiusMeters | 3 | Terrain paint exclusion radius. |
| LocationCooldownHours | 24 | Native world hours between renewals within five metres. |
| MaxPending | 1024 | Maximum pending renewals. |
| ScanRangeMeters | 64 | Processing range around a rotating ready player. |
| IntervalSeconds | 2 | Processing interval. |

Ranges and selection rules are in the [specification](../specs/forest-renewal.md). DebugMode follows the [shared debug/suppression policy](../server-setup.md#diagnostics).

## Diagnostics

Unknown structure-context samples include the blocking object, prefab and specific missing/unsupported condition. Unchanged blockers combine across affected stumps and repeat at most once per five minutes. Stage timings distinguish scope scanning and structure checks from terrain preparation, ledger serialization and world mutation. Known distant structure footprints use a conservative early distance check; unsupported geometry continues to postpone renewal.

Normal output groups confirmed results per completed pass and preserves failures and lifecycle notices. Optional Debug summaries describe changed current scope and result counts at most once per minute. `scope_now` counts pending renewals; `results_pass` counts completed operations in the latest pass; `results_total` counts them during the current controller lifetime. Counts restart when the controller is recreated. Empty unchanged scopes are quiet. Bounded decision samples identify standing-stump replacement, cleared-position planting, structure buffers, plant/tree/terrain/cooldown exclusion and unknown structure context.

See [shared safety and delivery limitations](../limitations.md#new-world-features) and [server setup](../server-setup.md).

Ledger writes run on one background worker. Sapling placement waits for durable acknowledgement and another main-thread exclusion check. Native growth still determines when the sapling becomes a tree. Debug separates background ledger I/O duration from controller processing time and reports snapshot, terrain-context and world-mutation stages.
