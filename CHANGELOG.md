# Changelog

## 0.2.6

- Fixed the images on HeimKit’s Thunderstore Details page, including the feature overview and HeimDefender illustration.

## 0.2.5

- Added **HeimDefender**, a host feature that keeps player-built Ballistas from targeting players and tamed animals. Hosts can also enable automatic bolt refills.
- **SharedMap** adds nearby discoveries to your personal map as you explore, even on vanilla servers. On HeimKit hosts, markers follow the host’s settings and merge with cartography table pins. On a HeimKit client, an unlinked standalone portal appears white and shows **(Unconnected)**. Its pin updates when Valheim reports a change.
- **TameTravel** improves portal travel for following wolves and Dead Raiser skeletons, and helps tames find room to land at portals inside buildings and ruins.
- **StationSupply** can start an empty station directly from its supply chest. Shield generators can draw fuel from the nearest eligible public chest on any side of the fuel hatch.
- Added **Server Diagnostics** to **F7 → Server Settings** so hosts and authorized admins can adjust server logging during play.
- HeimKit now supports **Valheim 1.0.31**, including servers configured to require the audited game version.

## 0.2.4

- **Run HeimKit in your own Valheim world.** Install it in the Windows game to play solo, or choose **Start Server** to play with friends. Guests can join and use the host’s gameplay features without installing HeimKit.
- **Manage your world in-game with F7.** Open [HeimKit Settings](https://github.com/blancanales/HeimKit/blob/v0.2.4/docs/heimkit-settings.md) to turn features on or off and adjust settings for the world you host. On a compatible dedicated server with client support enabled, players can view settings and authorized admins can change them when the server allows it.
- **See who’s visited and manage bans.** World hosts and dedicated-server admins can view current players and past visitors in [Server Administration](https://github.com/blancanales/HeimKit/blob/v0.2.4/docs/heimkit-settings.md#moderate-players). When changes are allowed, they can ban or unban players even after they’ve left.
- **Bring followers through dungeon entrances and exits.** Following tames can enter or leave with you when they fit through the doorway and have room to land. Skeletons you raise with the Dead Raiser can follow you through linked portals and dungeon entrances in either direction. [TameTravel guide](https://github.com/blancanales/HeimKit/blob/v0.2.4/docs/features/tame-travel.md)
- **Find more landmarks on your shared map.** Use **Read Map** at a cartography table to see discovered Fuling villages, Draugr villages, Black Forest tower ruins, Swamp ruins, Mountain cabins, Charred Fortresses and Deep North villages. [SharedMap guide](https://github.com/blancanales/HeimKit/blob/v0.2.4/docs/features/shared-map.md#structure-markers)
- HeimKit now officially supports [Valheim 1.0.30](https://github.com/blancanales/HeimKit/blob/v0.2.4/docs/server-setup.md#supported-environment). This release fixes compatibility issues in SharedMap, TameTravel and StationSupply.
- HeimKit automatically throttles background activity when it detects performance issues.

## 0.2.3

- Added [StationSupply](https://github.com/blancanales/HeimKit/blob/v0.2.3/docs/features/station-supply.md), which automatically supplies smelters, kilns and other production stations from nearby public chests.
- SharedMap uses the native boss marker for the Queen’s dungeon and removes its earlier Infested Citadel pins from shared table data.
- Added Dvergr house pins for discovered guard towers, lighthouses and excavations, including ruins. Each kind has an independent switch that defaults enabled.

- Fixed a WeatherGuard slowdown that could persist after players explored or traveled across many areas in a long session.
- Reduced WeatherGuard's per-frame scene work at large bases. Set `[WeatherGuard] ReuseSceneLists` to `false` and restart to rebuild the extra scene lists every frame.

- Reduced repeated server work in WeatherGuard scene handling and incoming network inspection while preserving weather protection and ownership safeguards.

- Reduced HeimMender's routine server work on large bases. It now follows only damaged pieces, picked up as their health changes, instead of re-checking every building piece every two seconds. Quiet periods, hostile clearance and per-cycle limits are unchanged.
- HeimMender now restores a damaged base gradually instead of in fixed bursts. After damage stops and nearby untamed creatures leave, each piece is repaired at its own random point within the new `RepairSpreadSeconds` period (default 1320, about 22 minutes), so a base mends piece by piece over roughly a game day. The new `CatchUpIntervalSeconds` (default 15) sets how often HeimMender checks for pieces that have come due. Set `RepairSpreadSeconds` to `0` for prompt repairs, or `CatchUpIntervalSeconds` to `0` for fixed `IntervalSeconds` cycles. Existing configurations receive both settings automatically.
- Added discovered Ancient Root map markers with the last observed sap-reserve percentage. Empty roots stay marked as they regenerate; extractors and root levels are left under native game control.
- Added Black Marble resource markers for outdoor petrified skulls and ribs, including fractured mining forms, remaining-section percentages and depleted-marker cleanup. Uses the existing resource discovery, grouping and map-clutter settings.
- Moved the server-wide performance window and slow-stage breakdown to global Verbose Debug. The reports omit unavailable measurements.

## 0.2.2

- HeimKit now attempts to run after Valheim updates by default. The new `[Compatibility] AllowUnverifiedVersions` switch replaces the manual exact-version override; set it to `false` to require the audited baseline. See [server upgrade settings](https://github.com/blancanales/HeimKit/blob/v0.2.2/docs/server-setup.md#server-upgrades).
- Before enabling features on a newer, unverified Valheim version, HeimKit creates a verified backup of the complete local world, current configuration and existing SharedMap/ForestRenewal data. A persistent record reuses that checkpoint across restarts for the same world and HeimKit/Valheim version combination. See [automatic world backups](https://github.com/blancanales/HeimKit/blob/v0.2.2/docs/world-backups.md#automatic-upgrade-backups).
- Features that encounter disabling errors during startup or later play remain disabled on subsequent starts for that world/version combination, overriding `Enabled = true` without rewriting configuration. Healthy features continue independently. Logs identify the failure record and associated checkpoint; see [persistent feature disables](https://github.com/blancanales/HeimKit/blob/v0.2.2/docs/server-setup.md#persistent-feature-disables).
- Installing a different HeimKit version automatically retries features enabled in configuration using a separate failure record, preserving earlier failure history. Old disable decisions do not block the upgraded mod.
- Existing configuration files are migrated automatically: the obsolete `SupportedValheimVersion` entry is removed, the new switch is added when missing, and existing feature values are preserved. See [configuration migration](https://github.com/blancanales/HeimKit/blob/v0.2.2/docs/server-setup.md#configuration-migration) and [compatibility limitations](https://github.com/blancanales/HeimKit/blob/v0.2.2/docs/limitations.md#server-observations-and-logs).

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
