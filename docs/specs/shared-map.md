# SharedMap specification

## Purpose and boundaries

SharedMap is an independently configurable HeimKit module for sharing exploration and discovered points of interest through vanilla cartography tables on a Windows dedicated server. Its display name and configuration section are `SharedMap`; its source namespace follows the repository convention `HeimKit.Features.SharedMap`. It belongs in the existing HeimKit DLL rather than requiring another plugin or any client installation.

The current implementation is a blank module containing configuration, compatibility checks and the shared player-presence lifecycle only. Supported environments belong in [server setup](../server-setup.md#supported-environment), and implementation must follow [contributing](../contributing.md) and [architecture](../architecture.md). The [usage guide](../features/shared-map.md) describes its present behavior.

## Current module contract

`SharedMapSettings.cs` binds `[SharedMap] Enabled = false` and `DebugMode = false` through the central loader. It uses the global diagnostics setting and shared suppression resolver without generating the hidden key. The optional reference configuration matches these defaults. Existing settings remain preserved; restart applies edits.

`SharedMap.cs` implements `IServerModule` and receives Plugin-owned PlayerServices. Plugin constructs and registers it in the configuration callback alongside the other modules. Initialize resets prior resources, logs configured enablement and debug policy, checks the shared exact game-version policy and subscribes only when enabled and compatible. An enabled startup message explicitly says mapping behavior is not implemented. Disabled or incompatible modules do not subscribe or process world state.

Tick checks Enabled, joined-player occupancy and native PlayerServices readiness. The existing PlayerProcessingNotices supplies one Ready entry when native services are available and Debug-only processing transitions. Its occupied acknowledgement describes the blank lifecycle, not successful discovery or map publication. Empty or unavailable presence clears its occupied state; shutdown, reinitialization and failure remove only its own subscription. There are no gameplay resources, activity leases, controllers, custom RPCs, patches, discovery scans, table changes or persistent map files. Runtime errors disable SharedMap independently.

Only settings and the main module are needed for this blank addition. Add a controller, diagnostic formatter or models only when an implemented responsibility requires them. No discovery mode, radius, category, enrollment or persistence defaults are selected by this scaffold. The remaining sections retain the requested mapping design and feasibility boundaries; they do not describe available gameplay functionality.

The server must not expose undiscovered world locations, inspect client save files, require custom client RPC handlers or modify client installations. Players receive updates by using a table's normal Read Map interaction. There is no continuous push into personal minimaps.

## Native cartography behavior

The dedicated-server game assembly exposes the following relevant behavior:

| Native path | Behavior and consequence |
| --- | --- |
| `Minimap.UpdateExplore` and `Explore` | Exploration is calculated around the local player. Continuous client exploration updates are not supplied by this cartography path to a server-side minimap. Server-observed travel is a separate source of evidence. |
| `MapTable.OnRead` | The client decompresses the table ZDO's `ZDOVars.s_data` bytes and passes them to `Minimap.AddSharedMapData`. A server-generated compatible payload can use this same read path. |
| `MapTable.OnWrite` and `Minimap.GetSharedMapData` | The client reads the table first, then sends compressed exploration and saved non-death pins to the table's network owner through the native `MapData` RPC. The upload includes personal pins without an automatic-private-sharing selector. |
| `MapTable.RPC_MapData` | Only the current owner stores the supplied bytes. A patch to this method on the dedicated server alone cannot intercept an RPC executed by a client owner. |
| `Minimap.AddSharedMapData` | Reading adds shared exploration and reconciles imported pins. Personal pins with owner zero or the local player's identity are preserved. Previously imported pins belonging to others can be removed if absent from the incoming table data. |
| `ZDOMan.ReleaseZDOS` and `ReleaseNearbyZDOS` | Native ownership assignment changes owners according to active areas. Claiming table ownership once does not establish durable server authority. |

These methods establish transport and ownership constraints; they do not establish that a player saw, entered or completed a particular location.

## Discovery evidence

There are two materially different discovery sources:

- **Exact uploaded exploration:** collect terrain from explicit vanilla table writes. This can retain the client's explored-pixel evidence, but requires players to write their maps and cannot supply automatic exploration as they travel. Exploration uploads must be separated from personal pins before entering the shared store. An upload's terrain may already include exploration read from other tables; it does not identify which player originally explored each pixel.
- **Observed travel:** sample usable joined-player character positions and record nearby terrain. This permits automatic collection without client installation, but approximates client exploration. Sampling can miss travel between observations. A proximity rule cannot establish exact client fog state, visual discovery, a location interaction or dungeon completion.

The discovery source requires an explicit product choice before gameplay implementation. Do not silently substitute travel inference for an exact-client-exploration requirement. If exact client exploration is mandatory, exclude automatic terrain collection between table writes. If travel inference is selected, describe the feature as travel-based discovery in both the guide and README, and define its radius, pixel mapping and limitations before implementing it.

Do not interpolate a revealed corridor between distant samples: teleporting and portal travel must not reveal the space between endpoints. Missing, loading, dead or invalid character observations must not create discoveries. Existing exploration held only in client saves cannot be recovered automatically.

## Location eligibility

Only locations satisfying the selected discovery-evidence rule may become shared pins. Enumeration of `ZoneSystem` location instances is candidate lookup, never discovery evidence by itself. World generation, placement, globally advertised icons, boss-location stones and portal connections must not reveal otherwise undiscovered destinations through SharedMap.

Categories are independently selectable: dungeons/crypts/caves/burial chambers, boss altars, merchants, portals and an exact administrator-configured point-of-interest allowlist. Use known native location or networked-prefab identities rather than broad name substrings. Unsupported identifiers must be reported and skipped. Do not claim complete category coverage without defining its actual identifiers.

Mark dungeon entrances rather than underground room coordinates. Require evidence at each portal separately; a discovered portal must never expose its partner. Portal names come from the discovered endpoint's replicated tag, with a bounded fallback label for unnamed portals. Claims about entering, clearing or completing locations are excluded.

Use vanilla-supported pin types and bounded names. Custom icons, client-local UI controls, automatic client notices and map-opening behavior requiring a client patch are excluded.

## Resource deposits and visibility

Copper and tin deposits require object discovery independently of terrain exploration. A vanilla table upload carries explored pixels and saved pins; it does not automatically identify ore in those pixels. Obtain candidate resource identities and positions from native networked world-object records, using an exact audited prefab allowlist. Static location records identify candidates such as cave entrances; they are not a complete resource catalog. Do not generate or reveal undiscovered zones to find deposits.

Distinguish these evidence levels before selecting a resource discovery policy:

| Evidence | Meaning and limit |
| --- | --- |
| Networked deposit exists | The server knows an object and its position. This alone establishes no player discovery. |
| Usable player character is nearby | A configurable proximity rule can identify an encountered deposit. It can include deposits behind terrain or outside the player's view. |
| Server geometry permits a sight line | A conservative obstruction check can reduce false proximity discoveries where relevant geometry exists. Missing terrain or colliders means unknown, never a clear sight line. It cannot reproduce the client's camera, viewport, rendering, mist or actual attention. |
| Native resource hit message | `MineRock.Damage` sends `Hit`; `MineRock5.Damage` sends `RPC_Damage`. A passive server routing observer can investigate messages that traverse the server without moving simulation ownership. Calls addressed to the same client's owner can execute locally and never traverse the server, so this is not a complete mining-event stream. A hit request is not proof of damage, successful mining or visual discovery. Validate the actual sender, target and available attacker evidence. |
| Replicated resource health changes | Native mine-rock health is stored in ZDO data. A correlated accepted change can strengthen resource-use evidence; an isolated change cannot establish which player caused it or that the whole deposit is depleted. |

The inspected `Player.Update` performs hover detection only for its network owner, and `FindHoverObject` uses `GameCamera`. A dedicated-server observer cannot obtain a remote player's exact hovered object by calling that method. Character facing or a look-direction method is not a substitute for a client's third-person camera and view obstruction state. Exact automatic "the player saw this deposit" behavior is excluded without additional client reporting.

Resource encounter distance and terrain reveal radius must be separate settings. A revealed terrain pixel is insufficient evidence for every resource in that pixel. Exclude buried-resource radar under a sight-based policy. Visibility inference and stronger resource-use evidence must remain distinguishable in the behavior contract; neither is selected by this specification alone.

For optional cluster pins, retain individual eligible resource identities underneath a bounded group. Cluster only resources that independently passed the discovery policy; do not attach nearby undiscovered deposits or use their positions or counts. Limit total cluster extent so chains of adjacent deposits cannot merge into an unlimited region. A cluster pin must remain anchored to a discovered member. Confirm destruction/depletion through native object and resource-state semantics before removing members; an unloaded object is not a depleted deposit. Use ordinary vanilla pins and labels rather than client-only interactive grouping or custom icons.

## Related implementation references

- [Valheim Web Map](https://github.com/koenhendriks/Valheim-Web-Map): server-only browser mapping. `PlayerTracker.cs` reads replicated character positions; `CartographyTables.cs` separately collects table exploration and pins. This is evidence for collecting server data, not for automatically writing native tables.
- [WebMap fog collection](https://github.com/f00d4tehg0dz/valheim-webmap/blob/main/WebMap/World/Fog.cs) and [world-object scanning](https://github.com/f00d4tehg0dz/valheim-webmap/blob/main/WebMap/World/WorldSweep.cs): server-only position-radius inference and ZDO enumeration. Generated-zone recovery approximates prior travel; it does not establish object visibility and must not serve as a resource-discovery shortcut.
- [MapTracker visibility](https://github.com/slikfoul/MapTracker/blob/main/SightVisibility.cs): client-side camera frustum, eye/camera raycasts and mist checks illustrate information unavailable for equivalent remote-camera discovery on a dedicated server.
- [ServerSideMap](https://github.com/Mydayyy/Valheim-ServerSideMap) and [SmoothServer](https://github.com/MJensen01/SmoothServer): their live shared-map functionality uses client components. Server-stored maps or acceptance of vanilla connections does not establish server-only map collection.
- [Better Cartography Table](https://github.com/nbusseneau/BetterCartographyTable): its private/public pin controls depend on a client component. Vanilla-readable outputs do not make those controls available to an unmodified client.

## Shared data and personal markers

The canonical shared store contains accepted terrain and server-generated location records. Do not automatically import personal pins from table uploads or existing tables. Do not infer whether an uploaded personal pin is private from its name, icon or author alone. Explicit vanilla Write Map ordinarily shares saved non-death pins; the guide must explain how SharedMap handles that interaction.

Exclude death, bed, player, ping, shout and transient event markers from SharedMap-generated data. Death pins are already excluded by the inspected native upload producer; validate imported payloads independently. Never remove a personal death marker from the player's local map.

Deduplicate locations by a stable source identity: location identity and entrance position for static locations, ZDO identity for portals. Use bounded spatial comparison only to reconcile equivalent records, not to combine distinct nearby entrances or portals. Preserve identity across restarts and avoid creating another record merely because a label changes.

Cleanup applies to shared records: duplicates, malformed coordinates, unsupported marker types, disabled categories and confirmed destroyed endpoints. Absence from a loaded-object scan does not prove destruction. Keep imported native shared pins distinct from personal pins when explaining table reads: replacing a table's shared dataset can remove previously imported pins on the next read. Server-only operation cannot promise to preserve every historically imported pin on a vanilla client while also excluding those pins from table data.

Do not overwrite preexisting table data merely to enroll a table. Define explicit enrollment and preservation behavior before enabling automatic updates, including treatment of manually shared pins and existing exploration. A server cannot reconstruct personal markers already removed by a native table read.

## Table authority and synchronization

Maintain a canonical shared dataset independent of any one table. Publish compatible compressed cartography payloads to enrolled vanilla tables, including newly enrolled tables. Use a rotating work budget and changed revisions so unchanged data is not repeatedly compressed or sent. Tables outside active areas may receive updates later; reading a table can return its last replicated revision. No server-side patch can retroactively change a read the client already performed.

Before supporting automatic publication, define a table-specific authority mechanism covering native reassignment, remotely executed writes, ZDO replication, destruction and removal. Scope interception to enrolled table data and its native map-write route. Preserve ordinary table health, damage, repair, wards, removal and unrelated ZDO fields. Do not reuse WeatherGuard-owned hooks or require WeatherGuard to be enabled. A local `RPC_MapData` patch or periodic blind ZDO overwrite is insufficient.

Treat client input as untrusted. Bound compressed and decompressed bytes, grid dimensions, marker counts and label lengths before allocation or mutation. Validate format, finite coordinates, pin types, package boundaries and compatible map geometry; reject unsupported formats without replacing the last valid shared state. Never rely on an unbounded decompression helper for network input. Use a vanilla-compatible nonlocal owner identity for generated shared pins and preserve ordinary client pin reconciliation semantics.

## Persistence

Partition canonical data by native world identity, independently of character and transient peer identities. Save with a versioned format, bounded records and atomic replacement while retaining the last valid state on errors. Table ZDO persistence alone is insufficient because all tables may be removed. Never combine worlds by display name alone or infer an explored map from the world seed.

Do not silently start an empty replacement over an unreadable existing store. Report the failure and disable SharedMap independently. Persist accepted discoveries before considering their durable revision complete; distinguish an in-memory update, a completed storage write and a table publication request. Normal process or disk failure can still lose work since the last completed save.

## Configuration and lifecycle

Provide one normal `[SharedMap] Enabled` switch independent of all other modules, plus `DebugMode = false` using the shared [hidden suppression resolver](../server-setup.md#hidden-per-feature-debug-suppression). The central configuration loader owns creation, preservation and saving of settings in `heimkit.cfg`. Restart applies edits. Do not generate configuration entries for excluded functionality.

When mapping behavior is added, configuration must define bounded scan intervals, discovery distances, per-pass work limits and individual supported marker categories. Discovery distance must not bypass the selected terrain/discovery-evidence rule. Exact upload mode has no automatic travel-reveal setting. Mapping defaults and the reference configuration must be specified together with the selected discovery and enrollment behavior before gameplay processing is activated.

Use the existing exact game-version policy and check required native signatures and payload compatibility. Failed compatibility disables SharedMap without changing other modules or bypassing the version guard.

## Player-service integration

The blank module follows the [required hookup](../architecture.md#required-feature-hookup): Plugin injects its existing `PlayerServices`, and enabled compatible SharedMap retains one presence registration. SharedMap owns only that registration, occupancy and lifecycle notices. It checks PlayerServices native readiness, acquires no AFK lease and performs no independent empty-server work. Pause has no gameplay resources to dispose; shutdown and failure remove the registration idempotently. No controller is created for an empty responsibility.

When mapping behavior is implemented, ordinary discovery, upload interception, ownership control and table publication must belong to a presence-controlled `SharedMapController` and require ready dedicated-server networking, world objects, routing and location services. Use the shared peer snapshot and each usable character's replicated position; do not create another presence census or activity observer. No AFK lease is required for these planned behaviors.

Empty or unavailable presence releases the controller's hooks, authority, pending work and transient observations before acknowledging the pause. Retain the presence registration for reconnect. World-service replacement releases the old world's resources before initializing another controller. Failure, rollback and shutdown dispose owned resources independently and idempotently.

If persistence is implemented, flushes are the only intended independent empty-server responsibility, under the architecture's backup/save-management exception: an empty transition or shutdown flushes already accepted dirty data without scanning, discovering, claiming tables or sending gameplay messages. A module-owned store may retain this data across controller pauses, with world identity checked before reuse. Shutdown removes the presence subscription. Failure must not dispose shared player services.

## Logging

Follow the [shared logging contract](../architecture.md#logging). Normal output includes effective enablement, one native-service Ready event and immediate storage/compatibility/authority failures. Shared player services own ordinary occupancy notices.

At the end of a completed publication batch, emit one concise count of tables whose local canonical payload was updated; this does not assert client delivery or a player map change. Empty and unchanged batches stay quiet. Report a completed durable save only when its write succeeds; do not print per-player movement or per-pixel exploration at Info.

Optional Debug output owns bounded discovery samples, candidate rejection reasons, ownership transitions, pending revisions and scan/publication budgets. Label current, pass and interval counters separately. Preserve distinct actor identities where player evidence is included, and suppress repeat incidents according to the architecture. Operational counters and required persistence remain independent of debug eligibility.
