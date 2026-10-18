# SharedMap

SharedMap records exploration around server-observed players in one persistent community map. Collection starts without requiring a cartography table. Newly observed tables receive the community terrain, and accepted table writes are normalized on the server. Players retrieve the last replicated data using vanilla Read Map; no client installation is required.

SharedMap is enabled by default. PortalMarkerSync is its optional portal-management subfeature, also enabled by default. Turning off PortalMarkerSync leaves community exploration, resource discovery when enabled, persistence and death-pin filtering active. ResourceMarkerSync is a separate optional subfeature, enabled by default. Existing configuration values are preserved on upgrade.

![SharedMap workflow: explore, discover outdoor resources, share saved exploration and markers at cartography tables, then Read Map; independent portal and resource controls, pickaxe-discovered Silver and selected shared-pin removal](../images/shared-map-workflow.jpg)

## Shared exploration

The native loaded Minimap supplies radius, pixel size, grid size and sampling interval. Exploration follows vanilla's pixel-circle calculation, regardless of camera visibility. Dead, unavailable and interior-height character observations are excluded. Sampling can miss travel between observations; teleport endpoints never reveal a connecting corridor.

Valid exploration already stored on a table or received in a table write is merged into the community map. Previously accepted terrain is not erased by a later writer with less exploration. Table terrain enters each reader's vanilla shared-exploration layer. That layer has no individual owner; personally explored terrain remains separate and the vanilla shared-map display toggle remains available.

## Table data

SharedMap fills table map data when native creation/replication paths observe the table and during bounded nearby reconciliation. A player does not first need to write a map. The server changes only the map-data field and lets native revisions and replication distribute it. SharedMap does not take network ownership of tables, change their creators, or alter health, repair, destruction or wards.

Incoming accepted ZDO updates and server-local writes to the map field are filtered. Shared death pins are removed. Other supported pins, except explicitly selected removals and managed portal records described below, retain their original owner, author, name, position and checked state. This does not remove personal death markers from a player's map. Resource pins are added only when ResourceMarkerSync is enabled; cave/location markers are outside scope.

Unsupported or malformed incoming map fields are rejected in favor of the prior field. Existing malformed tables are left intact for review. Ordinary Write Map still uploads saved personal pins as vanilla does; selective private-pin sharing is not included. Empty tables without generated markers use exploration-only native data. Portal markers use a reserved shared service owner, independently of table network ownership.

Normalization happens when the server accepts replicated state. A client can briefly retain its own unsanitized local write until a newer server revision arrives, and an early table read can precede initialization or replication. SharedMap cannot retroactively change a read already performed on a vanilla client. Field-change logs describe server state, not proof of delivery or a player map read.

## Removing an unwanted shared pin

An ordinary player-created pin can remain in table data after local deletion: vanilla Write Map reads the table before uploading. Read Map can re-import another player's shared pin, whereas an absent pin attributed to the reader is not re-imported. This is why deleting your own pin and writing can succeed while another player's copy returns; later uploads from anyone retaining a copy can restore the table entry. SharedMap supports removal of a specific shared pin using its exact identity, preserving other pins with the same label elsewhere.

The active configuration directory contains `HeimKit/SharedMap/world-<world ID>.map`. Beside it, create a UTF-8 file with the same world prefix and extension `.pin-admin.request`. Its contents are one command:

- `find Vile` lists matching table pins at Info with each pin's ID, icon and XYZ coordinates. Matching labels are exact and case-sensitive.
- `remove <pin ID>` persistently excludes the selected pin and sweeps saved tables. Copy the complete ID for the intended location from the lookup result.
- `allow <pin ID>` removes that exclusion, permitting future uploads; it does not restore deleted table data.

After the plugin has loaded, requests need no server restart. They are consumed while SharedMap has joined players and native world services are ready. Each request file contains one command and is deleted after acceptance; create it again for the next command. Ensure the actual filename ends in `.pin-admin.request`, not `.request.txt`. Invalid requests remain for correction and produce a warning. Lookup and removal advance gradually without loading distant scenes. Results are limited to 64 distinct lookup pins, with excess results reported. Debugging is not required. The `.pin-removals` file and its backup retain exclusions across restarts. Other labels and locations remain eligible. Renamed, moved, icon-changed or owner-changed pins have new identities; these rules target specific records rather than disabling an automatic portal/resource source.

After a removal finishes and replicates, use Read Map. Personal client pins may still need manual deletion. The server cannot force deletion from personal maps or guarantee replacement when another nearby pin matches vanilla's distance rule. Exploration, portals and deposits are preserved.

## PortalMarkerSync

With PortalMarkerSync enabled, player-built portals in community-explored terrain are added automatically, including unconnected portals and saved portals far from players. Markers use the vanilla portal-shaped icon and the portal's tag as the name, without a special text prefix; blank tags use `NoName`. Portal records are discovered at the table scan interval and current names/positions are read from accepted server data. No player needs to use Write Map. Readers retrieve the markers using vanilla Read Map.

Eligibility uses the persistent community map, including terrain imported from older tables, rather than only the current session or current reader's travels. Portals in already explored terrain can therefore appear without anyone revisiting them. Terrain outside the community's explored area does not qualify.

PortalClusterDistanceMeters defaults to 60 metres. Nearby portals appear as one `Multi Portals (N)` marker at their centre, using the portal icon. Each group stays anchored to its first portal, selected in stable world-object order; portals must be within the configured horizontal distance of that anchor, so a chain of neighbours does not group a whole region. A group's span can reach twice the configured distance. A single portal keeps its tag. Set the distance to zero to show portals individually. Changes require restart.

Generated markers and every uploaded pin using the portal-shaped icon are managed portal data, regardless of their names or authors. Their names and positions are replaced by the current portal catalog; duplicates, old names, old locations and deleted portals do not remain in normalized table data. While PortalMarkerSync is enabled, reserve that icon for actual portals: a portal-icon pin used for a cave or another purpose will also be replaced in shared table data. Other icon types are preserved except for obsolete portal duplicates matching the current portal label (case-insensitive) within three horizontal metres of its published position. Disabling PortalMarkerSync removes service-owned generated pins as tables are normalized, while leaving terrain and player-owned pins intact.

Vanilla can keep an already present pin within one metre without updating its label when Read Map runs. PortalMarkerSync defaults true and works around this by shifting a renamed portal/group marker to another nearby publication position. A marker starts at the actual location; later label changes select a position more than 1.1 metres from the prior marker, at most about 2.83 metres from the actual portal or group centre. The portal itself never moves. Unchanged labels keep their chosen position across reads, reconnects and restarts. The rename workaround is always active while PortalMarkerSync is enabled.

This encourages vanilla to insert the renamed pin and remove the unmatched old shared pin during the same Read Map operation. Nearby other pins can still block insertion, personal pins are not forcibly removed, and a client missing enough renames can encounter a reused position. Deleted/recreated portal identities and regrouping can also affect replacement. The server cannot confirm client reads or force an automatic map refresh. Portal names/groups come from saved world records; publication slots are retained separately beside the terrain file.

## ResourceMarkerSync

ResourceMarkerSync records outdoor Copper, Tin, Silver, Obsidian and static Flametal deposits as players travel within the native exploration radius. Dots use short resource labels. No table needs to exist during discovery; choose Read Map later to receive the saved discoveries. This is independent of PortalMarkerSync and does not require clients to install anything.

Only allowlisted mineable deposits qualify. Scrap piles, dungeon/interior resources, giant weapons/armour, creatures (including lava Leviathans), dropped/pickable items, ordinary stone and player-built objects are excluded. Static Flametal uses the supported meteorite deposit record when present; the plugin does not generate it. Gold is excluded. Silver stays hidden until a direct pickaxe hit on the vein or a qualifying pickaxe impact within SilverDiscoveryRadiusMeters of it; the default is 10 horizontal metres. Digging nearby can reveal the nearest eligible vein without hitting Silver itself. A direct hit saves the struck vein even if its centre is farther away. Both paths reuse the saved discovery and update its percentage. Ordinary travel, Wishbone pings and old explored terrain do not unlock it. Only discovered Silver joins shared groups. Native generation, replication and sampling can delay detection or miss brief travel between observations.

ResourceClusterDistanceMeters defaults to 30 metres and groups only the same resource type around a fixed first discovery. Zero shows individual deposits. Nearby discovered Tin nodes display Tin (N), then Tin when only one remains, and disappear after the final confirmed removal. Counts describe discovered deposit nodes, not ore yields or mining fragments. Retained depleted anchors keep groups stable, and neighbouring chains cannot extend a group's reach. Copper and discovered Silver markers show remaining-section percentages, such as Copper 65% or Silver 65%. This includes the deposit's underground mining sections; it is not a promise of a particular ore yield or a measurement of exposed rock. Grouped Copper/Silver combines original and remaining section counts, including zero remaining for confirmed removed boulders. The last observed percentage persists when a record is unavailable. A resource label followed by ?% means progress is not yet known; a future valid observation can supply it. Discovering another boulder can increase a group's percentage. Historical exhausted groups show 0% when retained. There is no reliable buried indicator, so no below-ground claim is added. Other resource groups retain their short resource label.

ResourceRemoveDepleted defaults true. Only confirmed terminal destruction or complete valid depleted mining health retires a node; an intact deposit becoming a fractured mining object is still the same discovery. Unloaded/missing objects and unavailable damage information do not establish depletion. Removed objects whose destruction was not observed may retain markers. Setting this option false displays historical nodes too.

ResourceDiscoverPreviouslyExplored defaults false: old explored terrain alone does not discover deposits. Enabling it also scans saved eligible deposits in the accumulated community-explored terrain, including exploration imported from tables. It does not load distant scenes or generate terrain. Retrospective work runs only with joined players and advances gradually. Turning it off stops that extra discovery source; accepted discoveries still persist across restarts.

Resource pins have their own service owner. Player-created dots, including matching resource labels, retain their native records; the dot icon is not reserved. Tin count changes use persistent small publication offsets shared with the portal refresh mechanism. Vanilla can still retain a nearby existing pin without updating its name or icon. Choose Read Map again after updates; local stale-pin cleanup can remain necessary. Shared-map visibility affects these pins like other shared pins; there is no new per-resource client filter.

## Reading updates and existing pins

Use **Read Map** at a vanilla cartography table to retrieve the data currently available there. Exploration, portal and resource changes do not force an automatic refresh of an open player map. Tables near usable players are reconciled periodically; when several tables are nearby, the configured per-pass limit spreads updates across passes. A distant table is refreshed when the server observes it through the supported update paths or it becomes a nearby candidate.

Vanilla can retain any saved pin within one metre of an incoming marker without updating its name or icon. Different players may see different results from the same table because each has different existing pins. Removing a stale duplicate locally and choosing Read Map again may be necessary, even after the server has corrected the table. Keep the vanilla shared-map display enabled when viewing community exploration.

Server cleanup removes matching nearby obsolete records from table data and uses a small persistent marker offset when needed to encourage replacement. It cannot inspect or erase a player's personal map. Ordinary Write Map still follows vanilla sharing behavior; SharedMap does not provide private-pin selection or guaranteed per-client replacement.

## Configuration

| Setting in SharedMap | Default | Meaning |
| --- | --- | --- |
| Enabled | true | Independent enable switch. |
| PortalMarkerSync | true | Automatically maintain explored player-built portals and correct managed portal markers. |
| PortalClusterDistanceMeters | 60 | Group portals within this horizontal distance of the first portal; 0 to 100 metres, zero disables grouping. |
| ResourceMarkerSync | true | Record outdoor Copper, Tin, Silver, Obsidian and static Flametal deposits independently of portals. |
| ResourceDiscoverPreviouslyExplored | false | Also discover remaining deposits in previously community-explored terrain without a revisit; existing discoveries persist either way. |
| ResourceClusterDistanceMeters | 30 | Fixed-anchor horizontal grouping distance for same-type deposits; 0–100 metres, zero disables grouping. |
| SilverDiscoveryRadiusMeters | 10 | Pickaxe-impact discovery radius for the nearest Silver vein; 1 through 30 horizontal metres. |
| ResourceRemoveDepleted | true | Hide confirmed removed/depleted nodes; false retains historical nodes. Missing/unloaded state never establishes depletion. |
| TableIntervalSeconds | 10 | Nearby table reconciliation interval, 2 to 120 seconds. |
| MaxTablesPerPass | 1 | Table payloads per first-run import tick, periodic reconciliation pass or requested pin-administration tick, 1 to 8; incoming writes and new tables use their native event boundary. |
| SaveIntervalSeconds | 60 | Dirty community-map save interval, 10 to 600 seconds. |
| PinDebugNameFilter | empty | Focus per-pin Debug detail on an exact label, such as Vile; empty samples all labels. |
| DebugMode | false | Optional feature diagnostics. |

Stop the server before editing and restart to apply settings. The central loader preserves existing values and adds missing defaults. An existing false value for PortalMarkerSync or ResourceMarkerSync stays false; enable the subcomponent explicitly if desired after upgrading. Radius and sampling interval come from the native component rather than a separate radar setting. SharedMap uses player-presence services without acquiring AFK tracking. Empty or unavailable presence removes its gameplay hooks and controller; accepted map data remains available for the next joined session.

## First startup

When this world has no saved community map, SharedMap first scans existing saved cartography tables across the world, including distant tables. It merges valid exploration into its own file before starting player capture. This read-only import can run with no players online and does not change table data. Invalid payloads are skipped with a startup warning.

Even if there are no tables or no exploration to import, an empty map file records completion. Later restarts load that file and skip the initial scan. An interrupted import is retried; an unreadable or corrupt existing community file remains an error.

## Storage and recovery

The community terrain lives in `HeimKit/SharedMap/world-<world UID in hexadecimal>.map` beneath the active BepInEx configuration directory. It is separate from the native world save and exists independently of tables. Its world identity and grid metadata are checked on load. Corrupt, unreadable, incompatible or truncated files disable SharedMap rather than starting an empty replacement.

The matching `.portal-pins` file retains marker names, publication slots and last published positions for the rename workaround. Portal identities follow their saved prefab and position, so native object-ID changes on restart do not assign a different portal's name history. Earlier state is reassociated only by a unique nearby actual location; ambiguous matches start fresh. Its bounded records, world identity and checksum are validated before use. Missing state starts fresh; corrupt state or a missing primary file with a retained backup disables SharedMap rather than silently overwriting it. All stores use the configured dirty-save interval and lifecycle flushes. A crash can lose publication choices since the last completed save.

The matching .resources file retains resource discoveries, depletion evidence and fixed anchor identities; .resource-pins retains resource publication positions/names. These are separate from tables and native world saves. Native object IDs can change on restart; saved resource types and positions are used to reassociate discoveries conservatively. The world-specific `.pin-removals` file separately retains selected pin exclusions, saved atomically when an admin command is accepted; lookup request files are transient and are not backup state. Corruption or orphaned backups never silently create replacements. Disabling resource markers retains these files while removing resource-service pins from normalized tables.

Dirty data is saved periodically and flushed on empty/unavailable presence, world changes, failure and shutdown. Saves use a flushed temporary file and atomic replacement; the prior map is retained as `.bak`. Unchanged terrain does not trigger a disk write. A crash can lose observations since the last completed save. Saving accepted terrain and the first-run read-only import can run without joined players. Ordinary table reconciliation and new player exploration wait for joined players.

Include this map directory with the corresponding world backup, after stopping the server. Restore world and community data from the same backup point to avoid revealing exploration retained from a later world state. The file uses world UID rather than display name. A radius/interval change may retain existing pixels, but a different grid or pixel scale must not reinterpret them. See [world backups](../world-backups.md#sharedmap-data).

## Diagnostics

Startup, readiness and failures follow the shared logging contract. A completed first-run import reports tables found, valid payloads imported and explored pixels saved; invalid payloads produce one separate summary warning. Normal result messages group changed tables and removed shared death pins by completed incoming packet or reconciliation pass. Empty or unchanged operations stay quiet. Invalid existing tables and rejected incoming fields produce bounded warnings; storage failures disable only SharedMap and remain visible with debug off.

Opening a world's community store reports at Info whether an existing map loaded, including its explored-pixel count and skipped import, or an absent map triggered first-run import. Corrupt stores remain failures rather than triggering an import. These messages occur at world/store initialization, not on each player join or scan.

Optional Debug output identifies the selected world UID and community-file path at store initialization, and reports native geometry, current explored-pixel count, latest sampling/table scope and completed saves. See the [debug and hidden suppression contract](../server-setup.md#hidden-per-feature-debug-suppression), [technical specification](../specs/shared-map.md), [presence lifecycle](../architecture.md#required-feature-hookup) and [supported environment](../server-setup.md#supported-environment).

Debug publication details include old/new names, actual and published positions, chosen slot and distance from the previous marker. Separate table details include the table ID, data revision and update reason. Each stream is capped at six records per minute; these describe server decisions and field changes, not client delivery. Summaries distinguish new pixels from player circles and table imports since the current controller started; those counters reset when its gameplay controller is recreated. Refresh-state load/save evidence is also optional Debug output. Gameplay collection, slot selection and saving continue independently of debug settings.

Portal catalog Debug summaries report registered records, eligible explored portals, excluded records, resulting grouped marker count and clustering distance, bounded to one summary per minute. Separate removal details identify a marker's identity and prior label when it leaves publication state because it is no longer eligible or has regrouped, bounded to six records per minute. Removal evidence describes server publication state, not confirmed client cleanup.

Catalog summaries identify persisted community exploration as the eligibility source and include the native pin type. Up to six rotating publication samples per minute include marker identity, label, published position and icon type, so stable markers omitted by the initial change-event budget are observable over subsequent scans. These samples do not inspect personal client pins or confirm client rendering.

When Debug is enabled, table normalization reports preserved pins whose labels match generated portal labels without regard to case. Up to six records per minute identify the preserved icon, owner and position alongside the generated portal icon type. The evidence distinguishes removed nearby duplicates from preserved distant matching labels; it does not confirm client rendering.

Resource results group newly recorded discoveries and confirmed removals at the table reconciliation interval, even when no table exists. Unchanged operations stay quiet. Optional resource Debug output reports discovery-record counts, the last prepared marker count and retrospective mode once per minute, plus up to six publication details per minute. These describe server observations and publication choices, not a confirmed client read.

Silver discovery uses observed pickaxe impacts and, for client-owner digging, recent pickaxe animation correlated with newly accepted terrain lowering. Missing baselines or delayed/ambiguous evidence leave Silver hidden and can require another impact. Earlier stored Silver records without mining-discovery evidence remain hidden until qualifying discovery. No terrain or mining behaviour is changed.

Pin-change Debug logs show additions, removals and checked/author changes between previous, received and normalized table data. These include ordinary player-created pins, even when HeimKit does not need to rewrite the accepted upload. Details identify label, position, icon, original owner and the replication sender when available. The sender may be the table-owning client forwarding another player's write; it is not proof of who clicked Write Map. Set PinDebugNameFilter to an exact label to focus detail; summary counts still include all labels. Player-pin details have separate limits from automatic markers. Output remains sampled and bounded, with omitted-evidence counts; malformed snapshots never establish a deletion. Debug logging does not change pin processing or confirm client display.
