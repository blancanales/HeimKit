# Changelog

## 0.1.55

- Added optional InfiniteTorches refilling for campfires, hearths and bonfires. IncludeFireplaces defaults off, with separate Campfires, Hearths and Bonfires controls for choosing which types receive free fuel.
- Added DungeonMarkerSync, enabled by default, with persistent labeled hammer markers at supported dungeon exterior entrances.
- Added optional dungeon discovery in previously explored community terrain, disabled by default and independent of resource discovery.
- Added AllowPlayerMapWrites so server owners can reject player uploads while automatic exploration, markers and Read Map continue.
- Added short remaining-pickup percentages for supported dungeons, including permanent core, Fenris Hair and ancient gemstone pickups. DungeonLootProgress independently hides percentages while retaining entrance markers and saved progress.
- Added optional shared X checkmarks, off by default, with persistent state and vanilla marker-adoption handling.
- Added RunestoneMarkerSync and SpawnerMarkerSync, both enabled by default, for placed lore runestones and visible, player-destructible recurring spawner objects. Hidden creature-spawn points are excluded. Each has one independent switch and automatically includes eligible points in community-explored terrain.
- Added a warning when enabled spawner discovery has no compatible source types, with catalog rejection details available in Debug logs.
- Removed confirmed destroyed spawner markers while retaining discovery history across restarts; killing a spawned creature does not remove its source marker.
- Suppressed duplicate dungeon publications within ten metres of a fixed entrance anchor and clean nearby uploaded dungeon-labeled hammer copies, including stale percentage labels. Preserve other player notes and separate saved entrance histories.
- Added configurable resource-marker spacing: hide resource pins within ten metres of another generated landmark by default, retaining resource history and depletion tracking.
- Reduced packet-copying, repeated map decompression and temporary allocations in dungeon scans, player tracking and routed-message observation while retaining existing scan intervals and validation.
- Rejected malformed compressed map uploads while preserving prior table data.
- Updated SharedMap guides, marker backup coverage and illustrations, with shared safety/client limitations collected in one page.

## 0.1.54

- Added SharedMap resource markers for outdoor Copper, Tin, Silver, Obsidian and supported static Flametal deposits, with independent controls enabled by default and no client mod required.
- Group nearby same-type discoveries, show remaining Tin node counts, and display remaining-section percentages for Copper and Silver. Silver stays hidden until qualifying pickaxe discovery.
- Persist resource discoveries before any cartography table exists and share them through Read Map. Previously explored discovery remains separately optional and off by default; missing or unloaded deposits are not treated as depleted.
- Added selected shared-pin lookup and persistent removal by name/location identity, preventing later uploads of the selected record while preserving other pins and exploration.
- Added detailed shared-pin change diagnostics and named player connection/disconnection notices.
- Fixed portal marker history being associated with different portals after world-object IDs change on restart.
- Updated SharedMap and feature-overview illustrations and reduced documentation image sizes.

## 0.1.53

- Added SharedMap: persistent community exploration collected around players, available through vanilla cartography tables without requiring players to write first or install a client mod.
- Added first-run import of exploration from existing cartography tables and filtering of shared death markers while preserving accepted terrain.
- Added optional PortalMarkerSync with named portal markers, NoName for blank tags, and Multi Portals groups using a configurable 60-metre default distance.
- Added portal rename, deletion and stale-marker reconciliation, including cleanup of matching nearby legacy pins. Players receive changes by reading a table; existing client pins may still require local duplicate removal and another read.
- SharedMap and PortalMarkerSync default enabled and can be controlled independently. Back up SharedMap's separate terrain and marker-state files with the world.
- Added illustrated feature guides and documented configuration, backup requirements and vanilla map limitations.

## 0.1.52

- Fixed stationary players failing to become AFK while health declines. Health loss, including incoming damage and food decay, no longer resets the AFK timer or ends AFK status.
- Added `PlayerActivity.DebugMode` for shared activity diagnostics independently of sleep and raid debugging, with the existing global inheritance and manual suppression controls.
- Kept ordinary AFK entry and exit notices visible at Info regardless of debug settings.

## 0.1.51

- Added this changelog to the Thunderstore package so release notes appear on the package page.
- Made release notes mandatory for future Thunderstore packages, with a matching current-version heading and at least one change entry.

## 0.1.50

- Updated the built-in Valheim compatibility requirement to the current [supported environment](https://github.com/blancanales/HeimKit/blob/feature/server-qol/docs/server-setup.md#supported-environment).
- Added the hidden `Compatibility.SupportedValheimVersion` setting to authorize one exact game version while retaining runtime API checks and independent feature switches.
- Added validation and startup warnings for manual version requirements, with fallback to the built-in requirement for invalid values.
- Updated compatibility and configuration documentation for all four features.
