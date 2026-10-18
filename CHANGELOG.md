# Changelog

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
