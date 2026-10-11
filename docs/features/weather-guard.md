# WeatherGuard

Automatic weather protection for existing and newly player-built structures, preserving their creators. Activation does not require repair, logout or rejoin. World-created claiming is inactive.

## Usage and scope

Enable the feature with `[WeatherGuard] Enabled = true` in the loader-provided `heimkit.cfg`, then restart the server. See [server setup](../server-setup.md) for installation and shared configuration.

Protection can wait for server context loading. The scope is static player-built structural categories with rain/wetness wear; containers, moving objects, non-building categories and already rain-immune materials are excluded. The weather gate also suppresses underwater wetness damage. Snow, Ashlands/lava and other biome hazards are not suppressed.

`Diagnostics.DebugMode` controls bounded troubleshooting output. See the [specification](../specs/weather-guard.md) for log semantics, eligibility, processing bounds and ownership behavior.
