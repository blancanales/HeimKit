# SharedMap specification

## Scope

SharedMap owns persistent community exploration, vanilla cartography-table map fields, and independently configured portal, resource, dungeon, runestone and recurring-spawner markers. It runs on the Windows dedicated server with unmodified clients. Follow [architecture](../architecture.md), [contributing](../contributing.md), [supported environment](../server-setup.md#supported-environment) and the [user guide](../features/shared-map.md). Gameplay/client limitations are maintained once in [safety and limitations](../limitations.md#sharedmap).

## Exploration and accepted table data

Use the loaded Minimap's grid size, pixel size, exploration radius and interval. Match native WorldToPixel rounding and the integer pixel circle with radius ceil(radius / pixel size). Reject invalid geometry rather than substituting defaults.

Sample the shared peer snapshot: ready joined characters, valid owner-matching character ZDOs, finite positive health, finite positions, and no dead/interior-height state. Public player-map visibility is irrelevant. Do not substitute connection reference points. Reveal endpoint circles without interpolating travel.

Union fully validated existing/accepted table exploration with the community mask. Preserve prior accepted terrain when a later upload contains less. The player-write policy selects the prior server field before normalization when uploads are disabled. Readers receive vanilla shared exploration; no per-player terrain attribution is created.

## Community persistence

When the world has no community file, complete a first-run import before player capture/table hooks. Use native iterative prefab lookup to scan saved cartography ZDOs, including distant records. Deduplicate identities; process at most MaxTablesPerPass payloads per tick. Import valid terrain only, leave tables unchanged, and summarize invalid payloads once at completion. Save the complete union, including an empty checkpoint. Interrupted imports have no completion checkpoint and retry on startup. Existing damaged files or orphaned backups are storage failures.

Store `world-<world UID in hexadecimal>.map` under `HeimKit/SharedMap` in the loader-provided configuration directory. Validate schema, world UID, grid dimensions, pixel size, packed bitmap length and checksum. Incompatible or unreadable state disables SharedMap without replacement.

Dirty saves use SaveIntervalSeconds. Flush accepted state on empty/unavailable presence, world/geometry replacement, failure and shutdown. Write a unique temporary file beside the destination, flush it, then atomically replace while retaining `.bak`; unchanged state produces no write. Retain accepted state across ordinary pauses. Radius/interval changes may reuse pixels only when grid dimensions and pixel size still match. Backup/recovery procedures belong in [world backups](../world-backups.md#sharedmap-data).

## Automatic portals

PortalMarkerSync uses ZDOMan.GetPortalList at each TableIntervalSeconds pass. On normalization, resolve live identities and require a nonzero Piece creator, finite exterior coordinates and community-explored terrain. Read accepted ZDOVars.s_tag; blank tags become `NoName`. Both connected and unconnected endpoints qualify.

Use Icon4 and reserved owner long.MinValue with empty author. While enabled, replace all table Icon4 pins from the authoritative catalog, regardless of prior owner/text. Remove stale managed names, positions, deleted endpoints and duplicates, including reuploads. For other icons, remove obsolete portal-label matches (case-insensitive) within three horizontal metres of a generated publication. Always preserve player-owned hammer pins. Other generated sources are replaced from their own catalogs. Disablement removes service-owned portal pins and preserves player-owned records.

Stable identity uses exact finite saved prefab/position, normalizing signed zero, instead of session ZDO IDs. Exclude ambiguous overlaps. Recover legacy native-ID publication state only when its actual position (published position minus slot offset) uniquely matches within half a metre. Unmatched/ambiguous histories start fresh.

PortalClusterDistanceMeters uses stable identity order. Assign each portal to the nearest existing first-portal anchor within the configured horizontal distance; use stable first-group tie handling. Anchors stay fixed and grouping is non-transitive. Singletons retain their tag. Groups publish `Multi Portals (N)` at the members' arithmetic XYZ centre. Zero disables grouping.

SharedMapMarkerRefresh keeps actual and published coordinates separate. New markers start at the actual point. Label changes select from nine slots: centre plus a two-metre grid, at most about 2.83 metres away, and more than 1.1 metres from the previous publication. Unchanged labels retain their slot. Stable portal/group-anchor identities own history. Persist labels, slots and last published positions in `.portal-pins`; prune no-longer-eligible identities and cap at 4096.

If obsolete-pin cleanup finds a record within the native matching range of the current publication, select and persist another slot more than 1.1 metres from it, then regenerate table data. Repeated stale uploads retain that correction. Portal objects and unrelated world fields never move.

## Automatic resource deposits

ResourceMarkerSync validates exact native prefab families and their mining/drop metadata:

| Resource | Prefabs |
| --- | --- |
| Copper | rock4_copper, MineRock_Copper and supported fractured successors |
| Tin | MineRock_Tin |
| Silver | silvervein, rock3_silver and supported fractured successors |
| Obsidian | MineRock_Obsidian |
| Static Flametal | MineRock_Meteorite and supported fractured successors |

Exclude interiors, scraps, giant weapons/armour, creatures/Leviathans, dropped/pickable items, player-built objects, ordinary stone and Gold. Missing/incompatible families produce one grouped warning; supported families remain available. Read persistent native ZDO coordinates and replicated mining health without ownership changes, instantiation or scene loading.

Ordinary discovery uses the same usable-character sampling and horizontal exploration radius as terrain. ResourceDiscoverPreviouslyExplored additionally accepts eligible records whose source position is in community-explored terrain. This optional scan advances one native prefab step and at most 256 queued records per tick; completed passes repeat no faster than TableIntervalSeconds. Nearby sampling uses rotating per-player candidate budgets. Silver always retains the mining rule below.

Persist discoveries and mining-section evidence in `.resources`. Assign increasing discovery identities; native ZDO IDs are transient bindings. Reassociate by same family/saved position only when unique within half a metre. Ambiguous matches stay unknown. Intact-to-fractured replacements continue one discovery. Keep confirmed depleted records as anchors/tombstones. Retire only native terminal destruction or fully validated health showing every section depleted. Missing records/invalid health retain the last accepted state. ResourceRemoveDepleted selects active-only or historical publication.

ResourceClusterDistanceMeters applies fixed, non-transitive anchors to the same type in discovery order, including depleted anchors. Publish at active members' arithmetic centre. Tin uses `Tin (N)`, `Tin` for one and no marker for zero active nodes. Copper/Silver uses ceil(100 × summed remaining sections / summed original sections), including zero remaining for confirmed depleted members. Any positive remainder displays at least 1%. Intact shells use validated successor section counts. Unknown totals use `?%`; retained exhausted groups use `0%`. New discoveries can increase the percentage. Other resources retain their short label.

Use Icon3 and reserved owner long.MinValue + 1. Replace only service-owned resource records; preserve player dots regardless of label. Reuse publication slots in `.resource-pins` for label/count changes. Cap discoveries at 65536 and generated resource markers at 4096. Rebuild grouping/publication caches only after accepted/configuration changes. Read earlier store formats without inventing section/mining evidence and save extended formats after real changes.

### Silver discovery observations

Subscribe to Plugin-owned PlayerRoutedMessages without acquiring AFK tracking. Validate authenticated character owner, health, position, equipped-pickaxe metadata, target, impact and player distance. An air swing alone is insufficient. Terrain requests must identify the native pickaxe dig operation and terrain-compiler target. Direct damage requires Pickaxes skill, positive pickaxe damage and the authenticated character as attacker.

A direct eligible-vein hit selects that vein regardless of centre distance. Otherwise choose the nearest undiscovered/nondepleted eligible Silver within SilverDiscoveryRadiusMeters of a qualifying impact, breaking ties by native identity. Use source coordinates. Persist the unlocked flag across fractured successors; older Silver records lacking evidence remain hidden until a qualifying impact. Only unlocked veins enter groups.

For client-local digging, compare validated terrain-compiler baselines after accepted ZDO deserialization: operation count advances, height decreases and last-operation coordinates match a recent authenticated pickaxe trigger. Fail closed on missing, malformed, delayed or ambiguous evidence. Bound per-actor triggers, rates and decoded arrays; clear transient evidence on pause/world change/failure. Observe accepted Silver mining fields and native destruction without modifying traffic, health or terrain.

### Resource publication spacing

ResourceMarkerHideDistanceMeters defaults to 10 horizontal metres, accepts 0–100 and uses zero to disable hiding. After all generated portal/resource/dungeon/runestone/spawner records have passed selected exclusions and their publication callbacks, hide only generated ResourceOwner pins whose final XZ lies at or within the configured distance of a final non-resource managed publication. Index those remaining portal/dungeon/runestone/spawner publications in horizontal cells and query neighboring cells; resources never become blockers. Do not consider unpublished/suppressed dungeon neighbors, excluded sources, disabled catalogs or ordinary personal pins. Compare marker/group publication positions, not individual deposit positions or heights. Apply hiding before the combined pin-count bound and serialization; stale service-owned uploads are replaced by the same accepted filtered catalog. Leave exploration, resource stores, cluster membership, health/depletion observations and issued publication history intact. Reconcile from the current catalogs each time so a resource can reappear after a blocking marker is removed or disabled, without rediscovery. Unchanged output remains byte-idempotent.

## Automatic dungeon entrances

DungeonMarkerSync combines persistent LocationProxy ZDO position/rotation with validated static Teleport offsets in exact allowlisted location prefabs. LocationInstance contains centers/placed state, not entrance rotation. Require a placed proxy; never generate zones/dungeons or instantiate locations for discovery.

| Location prefab | Label |
| --- | --- |
| Crypt2, Crypt3, Crypt4 | Burial Chamber |
| SunkenCrypt4 | Sunken Crypt |
| TrollCave02 | Troll Cave |
| MountainCave02 | Frost Cave |
| Mistlands_DvergrTownEntrance1, Mistlands_DvergrTownEntrance2 | Infested Mine |
| Mistlands_DvergrBossEntrance1 | Infested Citadel |
| Hildir_crypt | Smouldering Tomb |
| Hildir_cave | Howling Cavern |
| MorgenHole1, MorgenHole2, MorgenHole3 | Putrid Hole |
| PlaceofMystery3 | Reto's Tomb |
| BearCave | Bear Cave |
| TheHole01 | Winding Tunnels |
| MorkBorg | Mörkhalla |

Require enabled/active exterior Teleports, reciprocal interior targets, finite transforms and static active ancestry. Names alone are insufficient: ExteriorGateway commonly names the high-altitude interior exit. Reject randomized/condition-controlled ancestry and incompatible metadata. Cache validated offsets/deterministic sibling-index hierarchy paths, then release prefab references. Group unavailable-prefab warnings. Exclude Sealed Tower, ordinary buildings, TheDarkestHole and DN_Bossroom's conditional gateway.

Nearby sector queries include the maximum validated gateway offset. Queue each source/root/rotation with its original usable player sample; evaluate actual exterior distance against the native radius. Retrospective candidates instead require DungeonDiscoverPreviouslyExplored and community exploration at that exterior point. Evaluate at most 256 candidates per tick. Cap pending records at 65536. Retrospective scans advance at most one native LocationProxy step per tick and repeat complete passes no faster than TableIntervalSeconds.

Stable discovery identity hashes location type, exact placed-root coordinates and gateway hierarchy path, with signed zero normalized. Every gateway retains an independent discovery and visit/progress identity. Table publication applies the ten-metre duplicate rule below; it does not merge or delete stored gateways. Save accepted exterior XYZ/base label and pickup progress in `.entrances`, independent of tables. Read schemas 1 and 2 without inventing visits; write schema 3 after real changes, including one validated visit byte per discovery. Earlier zero-total progress remains unknown. Missing/unloaded sources retain accepted history. Cap records at 65536 and generated dungeon markers at 4096.

Use Icon2 (numeric 2), verified through Minimap.m_icons â†’ mapicon_hammer, and owner long.MinValue + 2. Keep the actual exterior point as the source; percentage/checkmark changes use the bounded publication offsets described below. Replace dungeon-service-owned records and apply selected-removal rules. For table publication, sort stable source identities and retain fixed actual-point anchors; suppress another dungeon source at or within ten horizontal metres of an existing anchor, including another family. Do not extend groups transitively. Choose anchors before selected exclusions so an excluded anchor does not expose a suppressed neighbor. Retain all discovery/visit/progress records and world objects. Remove uploaded hammer pins with a recognized dungeon family label (case-insensitive, optionally followed by an integer 0-100 percent) at or within ten horizontal metres of an authoritative anchor, regardless of uploaded owner/check state. This removes stale progress/adopted copies rather than treating an X as deletion intent. Preserve arbitrary hammer notes, other icons and distant pins. Cleanup requires an accepted dungeon catalog; disabling dungeon sync does not broadly remove player pins. Disablement removes generated pins during normalization while retaining discoveries.

### Dungeon pickup progress

DungeonLootProgress defaults true. Publish percentages only after a persistent visit established by observed interior entry or a fresh, fully matched inventory scan with a positive total and at least one picked flag. Share this state across players and discovered gateways of the same location. Scans apply only to discovered entrances; known world locations alone never qualify. Never infer visits from exterior proximity, explored terrain, table uploads, missing records, zero pickups, untouched inventories or old progress files alone. Reject inferred visits when the fresh total falls below the established inventory total. Match ready, living, owner-validated finite player positions using native Character.InInterior and Location.GetZoneLocation semantics: query the interior zone for exactly one persistent exterior LocationProxy, require its allowlisted metadata and reject multiple proxies or oversized record sets. Only mark existing discoveries; a later-discovered gateway inherits a sibling visit. Interior observations never reveal terrain, resources or new entrance markers. Continue visit capture when DungeonLootProgress is off.

After a visit, append a short remaining percentage to discovered dungeons with a verified nonrespawning persistent Pickable objective: Surtling Cores in Burial Chambers/Smouldering Tombs, Black Cores in Infested Mines, hanging Fenris Hair in Frost Caves/Howling Caverns, Frost Cores in Winding Tunnels, and ancient gemstones in Mörkhalla. Validate active metadata, persistent ZNetView, nonnull hide-on-pick and zero respawn. Associate saved pickup records with exact fixed-location or saved generated-room pickup transforms; never use exterior proximity as interior membership. Read saved room layouts without generating scenes. SharedMapDungeonLayout requires a four-byte positive room count followed by exactly 28 bytes per room: Int32 prefab hash, three Single position values and three Single Euler rotation values. Convert Euler angles with Quaternion.Euler as native Utils.ReadQuaternion does. Bound the count to 1024, validate complete length and finite transforms before returning any placements, and retain unknown status for rejected layouts. Count actual placed pickups, deduplicate native identities and treat missing/incomplete/ambiguous evidence as unknown. Zero means either every established pickup is picked or a supported inventory is established empty. A completely decoded layout with no objective candidates establishes empty immediately. Randomized candidate sites with zero actual saved pickups require complete successful scans spanning at least 30 seconds after the visit; malformed/missing layout or ambiguous evidence resets that settling period. An empty scan inside that settling period is inconclusive: established progress and its published label remain unchanged. Missing previously established pickups remain unknown even after settling. Sample inventories for discovered entrances before entry to recognize prior visits, but hide all percentages until entry or positive picked-item evidence establishes a visit. Persist accepted progress in the existing entrance store, retaining older format support.

Advance one target per occupied tick, load at most one new room template per tick, cache at most 2048 templates, bound layouts to 1024 rooms/65536 pickup points, and bound sector queries to 16 zones and 65536 returned records. Match generator identities/coordinates and native custom-interior placement before decoding saved room layouts. Require each accepted pickup record to match exactly one distinct layout site; multiple records claiming one site or a record matching multiple sites keep the inventory unknown. Share one interior inventory across a location's distinct gateways. Group changed progress at Info. Debug pickup and layout records are written only when a gateway's result changes and name the dungeon label, location prefab, gateway and entrance position; pickup records add total, picked, remaining percentage, change flag and reason, six per minute. A shared six-per-minute visit stream distinguishes observed entry (location type, observing peer and newly visited gateway count) from inferred prior visits (gateway and verified total/picked counts); repeated samples do not repeat the visit event. A separate six-per-minute layout stream adds room count, actual/expected payload bytes, validity and decode reason.

Reuse controller-owned pickup points, spatial buckets and identity/site sets between scans; pool at most 4096 bucket lists with capacity at most 64 each. Reject unrelated prefab hashes before spatial matching. Determine zero, one or multiple distinct matching sites without allocating a set per world record; repeated identical template sites still count as one site. Preserve scan cadence, complete-inventory decisions and all ambiguity checks.

### Shared player checkmarks and publication

SharePinCheckmarks defaults false. False strips player checkmarks from shared publication while leaving personal maps native. True accepts checked/unchecked transitions from valid allowed uploads relative to the previous accepted table; unchanged stale uploads do not clear authoritative state. Recognize managed markers only by their issued records and stable source identities, never by icon alone. Persist world-scoped checkmarks and publication history in `.pin-state` independently of tables, with world/schema/checksum validation, atomic backup replacement, a 32768-record limit and a 150 MiB input bound. Ordinary pin keys retain exact owner/label/source position/icon; generated keys use reserved owner and stable source identity. Ambiguous issued identities are rejected. Native grey-to-white adoption changes owner before checking: match only a newly owner-changed record with identical label/XYZ/icon whose unique previously issued managed counterpart is absent from the upload. Existing ordinary pins and uploads retaining the original counterpart are not adoptions. Persist bounded adopted fingerprints, route their check changes to the source and omit duplicate adopted copies from shared publication; personal client pins remain native. Checkmark details and publication changes each have six Debug samples per minute; accepted changes are grouped at the incoming-packet boundary at Info. Retain state across option disablement and pauses. Manual checkmarks do not alter pickup percentages.

Use bounded nine-slot publication offsets for changed dungeon percentages and shared checkmarks, preserving the actual exterior entrance/source separately. Slots are spaced two metres with a maximum additional horizontal displacement of about 2.83 metres; actual objects never move. Retain exact player pin owner, author, label, height and icon; only shared checked state and publication coordinates change. Apply selected-pin exclusions before publication. Client refresh limitations belong in the central limitations page.

## Automatic runestones and recurring spawners

RunestoneMarkerSync and SpawnerMarkerSync are the two independent landmark settings inside SharedMap, both defaulting true. Each enabled source discovers eligible points through nearby player observations or saved community exploration. Confirmed terminally destroyed spawners are hidden automatically. The central loader preserves recognized operator values and generates missing defaults. Disabling either source removes only its reserved table publications during reconciliation and retains its discovery file.

### Runestone eligibility and placement

The exact location allowlist is Runestone_Boars, Runestone_Greydwarfs, Runestone_Draugr, Runestone_Meadows, Runestone_BlackForest, Runestone_Swamps, Runestone_Mountains, Runestone_Plains, Runestone_Mistlands, Runestone_Ashlands and Runestone_DeepNorth. Labels are Boar Runestone, Greydwarf Runestone, Draugr Runestone, Meadows Runestone, Black Forest Runestone, Swamp Runestone, Mountain Runestone, Plains Runestone, Mistlands Runestone, Ashlands Runestone and Deep North Runestone.

Require an installed Location containing an enabled RuneStone with LodFadeInOut and StaticPhysics, without an individual ZNetView, Destructible or Vegvisir. Descendant ancestry must be active and contain only the inspected static lore-stone/Location behaviors. Reject conditional/randomized/animated ancestry, incompatible offsets and ambiguous sources. Boss trophy stones, Vegvisirs, decorative writing and other RuneStone-bearing objects never qualify. Boar stones are Meadows landmarks; their nine potential local boar points have zero respawn time and are not recurring farms.

Use actual persistent placed LocationProxy records, including saved position and normalized finite rotation. Resolve each validated hierarchy point as root position plus rotation times its root-relative offset. Remove the asset root rotation while retaining its scale, matching native SpawnLocation normalization; never use an unplaced location center. StaticPhysics only changes vertical placement, so horizontal discovery uses the placed stone point without running terrain physics. Keep each deterministic sibling-index path as a distinct source. Reading the stone is not required. No destruction inference is made for these static stones; unloading retains discoveries.

### Spawner eligibility and classification

Inspect every registered ZNetScene prefab incrementally rather than accepting a name prefix or suffix. Require an audited physical source prefab with its explicit publication label, a persistent root ZNetView, an enabled root Destructible with finite positive health, and stationary, active supported ancestry. Use the explicit source allowlist established from physical game assets; do not require runtime renderers or mesh references on a dedicated server. Require an enabled non-trigger Collider under the same root view and active nonconditional ancestry. Resolve the nearest view by walking the prefab hierarchy explicitly, including an inactive storage root, and reject colliders or areas owned by a nested view. Decorative child animation/flicker and damage areas on physical hitboxes do not disqualify the object; the spawn source itself retains the strict stationary ancestry checks. SpawnArea resolves its nearest parent ZNetView; CreatureSpawner requires its own ZNetView. Reject player pieces, items, characters, nonkinematic rigidbodies, animation/movement/conditional ancestry, nonpositive/nonfinite recurrence, absent creature metadata and invalid weighted tables. Area sources require positive interval/capacity and valid positive-weight creature entries. Repeating creature sources require effective positive m_respawnTimeMinuts. Names such as respawn_30 do not override the actual interval. Invisible spawn instructions, fish/NPC spawn points and hidden dungeon-population points are excluded even when persistent and recurring.

The physical source families are:

| Source family | Exact registered prefabs | Publication label |
| --- | --- | --- |
| Greydwarf nest / legacy heart | Spawner_GreydwarfNest, EvilHeart_Forest | Greydwarf Nest / Greydwarf Heart |
| Bone piles | BonePileSpawner, BonePileSpawner_swamp | Skeleton Bone Pile / Swamp Skeleton Bone Pile |
| Draugr pile | Spawner_DraugrPile | Draugr Pile |
| Charred sources | Spawner_CharredStone, Spawner_CharredStone_Elite, Spawner_CharredStone_event, Spawner_CharredCross | Charred Stone / Charred Elite Stone / Charred Event Stone / Charred Cross |
| Jotun mixed source | BlackIce_Core | Jotun Core |
| Elaking mixed sources | Spawner_Hole, Spawner_Hole_double | Elaking Hole / Elaking Double Hole |
| Mork blob source | BlobMorkBig | Mork Blob Source |

EvilHeart_Swamp is excluded because its weighted table contains a null prefab. Other legacy/event sources qualify only with valid current effective metadata and actual persistent world records; inclusion does not create locations or guarantee present-day world generation. All zero-respawn CreatureSpawner variants, including ordinary boar, boss, dungeon, animal and NPC points, are excluded. Creature death, wandering, blocked attempts and empty nearby populations do not change source eligibility. Native SpawnSystem biome/raid populations are outside this stationary-source catalog. Unregistered, nonpersistent or incompatible components are excluded.

Cache only names, labels and value offsets, not prefab objects. Explicit mixed-family labels name nests/piles/stones/holes/cores; all accepted sources use the explicit audited labels above. Never expose localization tokens. Two child areas in Spawner_Hole_double have distinct source paths and source points under one persistent parent identity; the same component encountered through multiple paths is not duplicated. A parent terminal event retires only its corresponding discovered child records. Resolve replicated scale for scale-synchronized sources within a finite positive four-times-template bound; omit unsupported scale rather than guessing.

### Discovery, identity, destruction and persistence

Reuse Plugin-owned PlayerServices and SharedMap's joined, ready, living owner-validated snapshots, native horizontal exploration radius and sampling cadence. Test distance at the actual source point, not at a proxy center. Each enabled landmark source also accepts only its own eligible points inside saved community exploration. Never generate zones, instantiate locations, take ownership, execute spawn code or load gameplay scenes. Exterior and interior sources remain separate; interior publication is omitted without a saved-layout/source membership and exterior gateway implementation. The central [discovery limitations](../limitations.md#discovery) define the resulting scope.

Inspect one registered prefab or one soft-reference location per occupied tick, releasing every loaded reference in finally. Rotate through at most 256 nearby records per player sample. Advance at most one iterative native prefab scan step per tick, enqueue at most 256 returned records and process at most 256 candidate records/actual points per tick. Bound queues, bindings, ambiguity sets, scan chunks and stores at 65536; cap each family at 4096 published markers. Repeat a completed retrospective pass no faster than TableIntervalSeconds. Cache unchanged publication catalogs. Overflow retains prior valid table data and accepted history instead of truncating output.

Runestone identities hash exact native location name, finite persistent proxy XYZ and deterministic stone hierarchy path. Spawner identities hash exact native source prefab, persistent root XZ and child path; native vertical StaticPhysics settling retains the accepted height and cannot create a new identity or bypass a tombstone. Simultaneous stacked persistent sources at the same horizontal identity are ambiguous and retain the last accepted state. Signed zero is normalized. Source actual XYZ stays separate from publication offsets. No distance-based merge or fuzzy reassociation is performed. Rebinding requires the same exact identity; multiple simultaneous persistent identities at one source key are ambiguous, retaining last accepted state. Distinct nearby sources with the same label remain independent. Session ZDO IDs are transient bindings only.

Subscribe additively to ZDOMan.m_onZDODestroyed while occupied. This runs at accepted HandleDestroyedZDO before native release; ordinary scene unloading has no retirement path. Never alter ownership, damage, native spawning, network payloads or another subscriber. Save terminal tombstones before table retirement. Retain terminal tombstones across restarts and hide retired sources from publication. Missing records, ownership transitions and ambiguous reassociation retain prior accepted state. Spawners are never reconstructed from proxy templates, preventing a destroyed source's location proxy from recreating it.

For schema-1 spawner histories, publication accepts only the explicit physical-source labels above: source prefab/path is hashed rather than stored reversibly. Suppress old hidden-point labels before applying publication limits, retain their saved records without inventing destruction, and keep accepted physical-source tombstones unchanged. Normal service-owner reconciliation removes stale hidden-point shared uploads; unrelated personal pins remain ordinary records.

Persist separate world-scoped `.runestones` and `.spawners` stores with schema 1, explicit source-kind byte, bounded labels/records, finite exterior coordinates, world identity and SHA-256 checksum. Serialize discoveries and terminal flags together. Read fully into temporary state before accepting. Flushed temporary files use atomic replacement and retain `.bak`; missing primary beside backup, corruption or incompatible state fails closed. Dirty-save intervals and lifecycle flushes follow community persistence. Retain accepted stores across pause; dispose queues, bindings, observers and metadata on pause/failure/shutdown. No AFK activity lease is used.

Use vanilla Icon3 (numeric 3), resolved through Minimap.m_icons to mapicon_pin. Owners are long.MinValue + 3 for runestones and long.MinValue + 4 for spawners, distinct from existing portal/resource/dungeon owners. Replace only service-owned publications; keep player dots and other families. Stale service-owned uploads are removed before rebuilding from the accepted catalog, so retired or disabled markers cannot recreate their source. Reuse issued identities, selected-pin exclusions, adoption handling, shared checkmarks and `.pin-state` offsets. Recognize grey-to-white adoption even with shared checkmarks disabled, suppress its duplicate publication, and retain ordinary unrelated pins. Ambiguous ownership edits lacking an issued counterpart are not treated as source deletion requests. Respect the existing combined 8192-pin and payload limits.

## Checked-pin administration boundary

The native Write Map workflow sends MapData to the current table owner. That owner writes the ZDO data field; subsequent server replication identifies the forwarding peer rather than necessarily the uploader. Native Minimap serializes owner/author/checkmark fields from client pin data. ZNet admin checks use authenticated socket/platform identities, but the checked record carries no binding from that authenticated identity to the particular click/upload across client-owned table forwarding. Table ownership, proximity, player display name and uploaded pin fields do not supply that missing link. A direct server-owned table RPC path does not establish attribution for the general client-owned workflow.

Checked uploads can change shared checkmarks only under SharePinCheckmarks; they do not authorize source deletion. Source-adoption deduplication removes the duplicate publication without retiring its source. Server-side `find`, `remove` and `allow` request-file administration remains the explicit authorization step. Checkmark state and existing removal decisions are retained. Client delivery/attribution constraints are maintained in [player uploads](../limitations.md#player-uploads).

## Player map-write policy

AllowPlayerMapWrites=true accepts uploads through ordinary validation/union. False selects prior server-accepted table data before importing any incoming terrain/pins, then normalizes that retained field. Reject changed, removed, empty or malformed incoming map fields and externally initiated server-local writes. SharedMap's scoped internal writes remain allowed. Keep unrelated accepted ZDO properties native.

After native deserialization, restore a rejected field with a fresh server revision and ordinary replication. On normalization failure, restore the previous field before isolating the module failure. Historical player pins and first-run import remain unchanged. Vanilla interaction behavior is defined in [upload limitations](../limitations.md#player-uploads).

## Selected table-pin removal

Filesystem-authorized administration uses `world-<UID>.pin-admin.request`, consumed during occupied/native-ready processing. Accept one bounded command: `find <exact label>`, `remove <pin ID>` or `allow <pin ID>`. Lookup scans saved table ZDOs incrementally, deduplicates the same record across tables, caps output at 64 matches and reports omissions. Advance at most one native scan step and MaxTablesPerPass payloads per tick. Preserve malformed tables and report skips.

Pin identity is SHA-256 of canonical original owner, exact label, XYZ and icon; checked/author changes do not evade removal. Validate finite coordinates and native record bounds. `remove` atomically saves the ID to `.pin-removals` before activating it, then sweeps tables through normal reconciliation. Filter selected IDs from existing data, accepted uploads and generated catalogs. `allow` removes a rule and permits later uploads. Persist up to 8192 IDs with world/schema/checksum validation and backup retention. A failed save never activates a rule. Clear transient scans on pause/failure/shutdown; retained rules reload on reconnect. Administration changes table data only.

## Table initialization and field filtering

Observe exact `piece_cartographytable` prefab assignment, accepted ZDO deserialization and nearby periodic scans. Initialize new/empty table data from the community mask without a prior Write Map. Reconcile at TableIntervalSeconds with MaxTablesPerPass payloads per periodic pass.

Modify only ZDOVars.s_data. Preserve network ownership, creator and unrelated world fields. Within authenticated RPC_ZDOData from ready peers, snapshot the prior field before native Deserialize and sanitize the accepted field before packet completion. Native old-revision rejection is not a newly accepted upload. Filter server-local writes to the same byte-array field. Finalizers restore nested hook/packet context and preserve native exceptions.

Validate compressed/decompressed size, exact grid length, boolean bytes, pin count/types, bounded UTF-8 labels/authors, finite XYZ, complete structure and trailing bytes before importing terrain. Reject malformed incoming fields in favor of prior data; leave malformed existing tables intact. Remove Death pins, selected removals and replaced service-owned records; retain unchanged native records byte-for-byte. Shared checkmark policy may change checked state and bounded publication coordinates; preserve owner, author, label, icon and height of ordinary surviving records.

Preserve native payload format. Empty tables without markers use exploration-only data; adding markers upgrades that format. Pin-bearing formats remain pin-bearing even with zero surviving pins. Cap generated markers at 4096 per source, total pins at 8192 and compressed/pin bytes at existing two-megabyte limits. Overflow preserves prior table data rather than publishing a partial catalog.

Within one normalization, decode and validate each prior/incoming payload once for terrain import, preflight, checkmark/adoption comparison and final reconciliation. The decoded input remains immutable; copy terrain only when adding pixels. A portal repositioning retry reuses that same validated input. Do not cache across operations by byte-array identity or skip validation of later uploads.

Successful table payload snapshots are bounded to 64 entries and sixteen MiB; larger payloads continue ordinary reconciliation. Nearby successful table reconciliation is cached by an exact payload snapshot and complete published inputs: exploration count, all five marker families, names/identities/coordinates, portal publication revision, shared pin state/adoption/checkmark revision, administrative removal revision and resource hiding distance. New uploads always use full validation. Changed publication state forces reconciliation; unchanged portal inputs reuse grouping and publication results. Terrain-map interval saves serialize an immutable snapshot on the main thread and run checksumming, durable flush and atomic replacement on one background worker. Only the completed snapshot pixel count is acknowledged; later changes remain dirty. Lifecycle flushing joins the outstanding writer and saves newer state before returning. File format and backups remain unchanged. Dungeon loot reconciliation retains its existing generated-layout, pickup and empty-inventory checks.

## Lifecycle and ownership of resources

Follow the [required player-service hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices. SharedMap owns presence registration, world mask, resource/dungeon discovery stores, shared pin state and portal/resource publication stores. SharedMapController owns sampling, discovery, resource depletion, portal/resource grouping, table normalization, pin administration and subscriptions. SharedMapDungeons owns dungeon metadata and transient encounter/retrospective queues. No AFK lease or local player census is created; Silver uses the shared routed observer independently.

Ordinary work requires occupied presence and dedicated-server world, scene, routing, ZoneSystem and Minimap services. Dispose controller hooks, destruction/routed subscriptions and queues before acknowledging empty/unavailable presence. Retain accepted stores for reconnect. World-readiness loss removes the controller; shutdown/rollback/failure independently release owned resources without disposing Plugin's shared services.

Dirty accepted-state saves and lifecycle flushes use the save-management exception. Absent-file first-run terrain import is a separate read-only offline-processing exception: it reads tables, saves only a completed checkpoint and installs no gameplay hooks. Discovery/retrospective scans do not use that exception.

All stores validate bounded records, world identity, schema and checksum. Missing stores start empty except orphaned backups; corruption never silently resets state. Use flushed temporary writes, atomic replacement and `.bak` retention. Dungeon/resource disablement retains discoveries. SharedMapMarkerClusters/MarkerRefresh are reused for portals/resources; MapMarker and the table writer/reconciler also serve dungeons.

Nearby dungeon/proxy and runestone/spawner discovery uses shared spatial candidate indexes. Retrospective world scans and dungeon loot reconciliation retain their existing bounded scheduling.

## Configuration

SharedMapSettings binds the following restart-required values in `[SharedMap]`; central configuration loading preserves existing values and generates missing defaults.

| Setting | Default | Range/contract |
| --- | --- | --- |
| Enabled | true | Independent module switch. |
| PortalMarkerSync | true | Portal catalog and portal-icon reconciliation. |
| PortalClusterDistanceMeters | 60 | 0–100 metres; zero disables grouping. |
| ResourceMarkerSync | true | Independent resource discovery/publication. |
| ResourceDiscoverPreviouslyExplored | false | Additional explored-terrain discovery; Silver rule remains. |
| ResourceClusterDistanceMeters | 30 | 0–100 metres; zero disables grouping. |
| ResourceMarkerHideDistanceMeters | 10 | Hide resource markers within this horizontal distance of another generated landmark; 0–100 metres, zero disables hiding. |
| SilverDiscoveryRadiusMeters | 10 | 1–30 horizontal metres. |
| ResourceRemoveDepleted | true | False publishes retained depleted history. |
| DungeonMarkerSync | true | Independent exterior entrance discovery/publication. |
| DungeonLootProgress | true | Remaining pickup percentages after observed entry or verified already-picked items. |
| SharePinCheckmarks | false | Accept player checkmark transitions from allowed uploads. |
| DungeonDiscoverPreviouslyExplored | false | Additional placed-entrance discovery in explored terrain. |
| RunestoneMarkerSync | true | Independent lore-stone discovery/publication, including community-explored points. |
| SpawnerMarkerSync | true | Independent recurring-source discovery/publication, including community-explored points and confirmed-destruction retirement. |
| AllowPlayerMapWrites | true | False rejects uploads before import. |
| TableIntervalSeconds | 10 | 2–120 seconds. |
| MaxTablesPerPass | 1 | 1–8 import/reconciliation/admin payloads. Incoming writes/new-table callbacks use their event boundary. |
| SaveIntervalSeconds | 60 | 10–600 seconds; lifecycle flushes do not wait. |
| PinDebugNameFilter | empty | Exact case-sensitive per-pin Debug filter; totals include all labels. |
| DebugMode | false | Shared global/feature policy and manual suppression. |

Native Minimap owns exploration geometry/cadence. See [debug policy](../server-setup.md#hidden-per-feature-debug-suppression).

## Logging

Use SharedMapDiagnostics and the [shared logging contract](../architecture.md#logging). Changed cleanup scope reports suppressed nearby dungeon publications and obsolete uploaded dungeon pins at Debug, capped at six records per minute; unchanged scopes stay quiet. These counts describe prepared normalization, while existing grouped table-change Info reports confirm accepted server table writes. Info covers enablement/readiness/failures, one storage decision per world initialization, completed terrain import, new discoveries/confirmed removals and changed table fields. Runestone/spawner discoveries and confirmed spawner retirements are grouped by the normal table pass at Info; unchanged passes stay quiet. Landmark Debug evidence is capped at six details per reason per minute; it distinguishes actual-point proximity, retrospective exploration, ambiguous persistent identity and accepted terminal destruction. The metadata catalog reports effective landmark switches, available family counts, audited spawner candidate count and grouped spawner rejection reasons once per occupied controller at Debug. An enabled spawner catalog with zero accepted families emits one Warning at that completed audit boundary; a disabled source does not emit this warning. Unknown prefab names outside the audited allowlist are silently excluded rather than counted as compatibility failures. Group results by TableIntervalSeconds pass or completed incoming packet; keep empty/unchanged batches quiet. Invalid startup payloads produce one completion warning. Persistence success stays Debug.

| Debug stream | Evidence and bound |
| --- | --- |
| Community scope | Geometry, player/table scope, explored pixels and controller-lifetime player/import additions. |
| Portal catalog | Registered/eligible/excluded/grouped counts, anchor distance, eligibility source and icon; at most one catalog summary per minute. |
| Portal publications/removals | Stable identity, label, actual/published position, slot, prior distance or removal reason; six details per stream per minute. |
| Portal conflicts | Preserved/removed matching labels with icon/owner/position; six per minute. |
| Resource catalog | Discovery count, prepared marker count and retrospective mode; one per minute. |
| Resource publications | Identity, label, actual/published point and slot; six per minute. |
| Dungeon startup | Validated prefab/gateway counts, retained records, radius/cadence and effective write/retrospective settings. |
| Dungeon discoveries | Source, identity, prefab/root, exterior point, original player sample and proximity distance/radius; six per minute. Changed scope summaries: one per minute. |
| Storage | Loaded/saved state counts and paths; saves are throttled by stream. |
| Table fields | Table ID, data revision and native event boundary; six per minute. |
| Rejected player writes | Changed-field count grouped by packet/pass; six reports per minute. |

Pin-change diagnostics compare validated previous, received and final lists even when normalization leaves bytes unchanged. Report added/removed/checked/author changes with duplicate multiplicity; renames, moves, owner/icon changes are removal/addition. Include exact native fields and authenticated replication sender when available. Gate decoding on effective Debug before collecting snapshots. Cap comparisons at 60 per minute, summaries at six per stage/minute and details at six per stage/action/source-family/minute, separating generated/player families. PinDebugNameFilter affects details only. Invalid snapshots emit bounded unavailability evidence. Diagnostic failures stay isolated from gameplay and persistence. Delivery/attribution interpretation is defined in [server observations and logs](../limitations.md#server-observations-and-logs).

## HeimKit Core integration

The performance rebuild in `src/HeimKit/Features/` consumes CandidateSubscription for tables, resources, dungeon and landmark context, PrefabRegistry metadata catalogs, PlayerServices routed input and ModuleScheduler sampling/table cadence; stores and publication rules stay feature-owned. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.

Placed-source geometry and gateway identities are cached by their exact source/template and transform. A changed transform rebuilds the corresponding points; accepted discovery and retirement policy remains unchanged. Resource progress reuses the decoded observation only while the same native record, prefab definition and data revision remain current. Pausing releases these transient caches; the existing durable stores remain authoritative.
