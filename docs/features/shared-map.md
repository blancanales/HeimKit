# SharedMap

SharedMap currently provides a blank, independently configurable module in the HeimKit DLL. It does not collect exploration, discover resources or locations, create pins, change cartography tables or persist shared map data. Players need no additional client installation for this module.

Its generated `[SharedMap]` section contains `Enabled = false` and `DebugMode = false`. Enabling it activates only its compatibility and player-presence lifecycle. Startup explicitly states that mapping behavior is not implemented. Stop the server before configuration edits and restart to apply them.

Enabled modules report native-service readiness once and acknowledge occupied/empty transitions through the shared player services. Ready means lifecycle readiness; it does not mean map synchronization is available. No activity lease, gameplay controller or network hooks are installed.

Diagnostics follow the shared [debug configuration and hidden suppression contract](../server-setup.md#hidden-per-feature-debug-suppression). Startup, compatibility warnings and failures remain independent of DebugMode. No discovery settings exist until their behavior is implemented.

See the [specification](../specs/shared-map.md) for the current module contract and mapping design boundaries, [architecture](../architecture.md#required-feature-hookup) for lifecycle rules and [supported environment](../server-setup.md#supported-environment) for compatibility.
