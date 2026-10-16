# Changelog

## 0.1.51

- Added this changelog to the Thunderstore package so release notes appear on the package page.
- Made release notes mandatory for future Thunderstore packages, with a matching current-version heading and at least one change entry.

## 0.1.50

- Updated the built-in Valheim compatibility requirement to the current [supported environment](https://github.com/blancanales/HeimKit/blob/feature/server-qol/docs/server-setup.md#supported-environment).
- Added the hidden `Compatibility.SupportedValheimVersion` setting to authorize one exact game version while retaining runtime API checks and independent feature switches.
- Added validation and startup warnings for manual version requirements, with fallback to the built-in requirement for invalid values.
- Updated compatibility and configuration documentation for all four features.
