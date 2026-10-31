# Changelog

## 0.2.1

- Updated the built-in Valheim compatibility requirement to the current [supported server environment](https://github.com/blancanales/HeimKit/blob/v0.2.1/docs/server-setup.md#supported-environment).
- Reduced WeatherGuard protection interruptions caused by unloaded creatures and loose items. Context checks now follow the game's roof/support collision layers while retaining support-giving pieces and shields as dependencies.
- After a game update, server owners can manually set `[Compatibility] SupportedValheimVersion` in the active `heimkit.cfg` to permit loading on another exact game version, subject to the existing runtime API checks. See [version override instructions](https://github.com/blancanales/HeimKit/blob/v0.2.1/docs/server-setup.md#hidden-version-override) for the setting, accepted format and restart behavior, and [compatibility limitations](https://github.com/blancanales/HeimKit/blob/v0.2.1/docs/limitations.md#server-observations-and-logs) for what the override does and does not establish.
- See [WeatherGuard coverage limitations](https://github.com/blancanales/HeimKit/blob/v0.2.1/docs/limitations.md#weatherguard-and-infinitetorches) for remaining terrain, instance-loading and vertically separated context constraints.

## 0.2.0

- Rebuilt shared server processing to reduce repeated world scans, prefab lookups and temporary memory allocations while retaining independent feature settings.
- Reduced repeated SharedMap table processing and moved routine community-exploration disk writes into background work.
- Added TameTravel: supported following tames can accompany players through linked player-built portals outside dungeons. Includes follow, stay and portal-wait commands, adjustable follow spacing, checked arrival placement and cleanup on logout/restart. Fresh follow commands can reclaim an offline player's stale follower.
- Added DoorKeeper: closes eligible player-built doors and gates after a configurable delay when the doorway is clear.
- Added ForestRenewal: renews supported stumps and confirmed cleared-stump locations with matching vanilla saplings, configurable delay/chance and clearance rules, and persistent renewal decisions.
- Added SeaSalvage: recovers eligible settled sunken loose items into floating cargo crates while preserving item data. Tombstones are excluded.
- Added HeimMender: gradually repairs damaged player-built structures without materials, with configurable cycles and nearby administrative repair requests. HeimMender settings reload during play.
- DoorKeeper, ForestRenewal, SeaSalvage, TameTravel and HeimMender default enabled and can each be disabled independently. Existing configuration values are preserved; missing settings are added automatically.
- Fixed SharedMap dungeon percentages and marker labels losing established progress while temporary empty pickup scans await confirmation.
- Redesigned logging with local timestamps, clear feature topics and Normal/Verbose debug detail. Repeated unchanged dungeon observations stay quiet, and failures retain their underlying exception details.
- Combined WeatherGuard's routine weather-prevention output into nonempty ten-minute summaries, flushing pending results when protection pauses or stops.
- Refreshed branding, documentation and feature illustrations, including workflows for all five new features and clearer SharedMap marker labels.

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
