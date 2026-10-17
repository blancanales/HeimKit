# Changelog

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
