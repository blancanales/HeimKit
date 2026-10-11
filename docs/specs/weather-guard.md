# WeatherGuard specification

## Purpose and scope

WeatherGuard automatically protects eligible player-built structural pieces from rain and wetness wear on a Windows Valheim dedicated server. Existing creator ownership is preserved. Activation requires no player repair, logout, rejoin or manual refresh. Unclaimed world-generated pieces are not protected or claimed by the current code.

The module uses BepInEx and Harmony and is designed for unmodified clients. Supported game and loader versions are documented in [README.md](../../README.md#supported-environment). Local builds and automated checks pass. Server terrain loading, ownership handover, rain protection, combat, support and multiplayer behavior have not yet been verified in-game.

## Configuration

| Section | Setting | Default | Behavior |
| --- | --- | --- | --- |
| WeatherGuard | Enabled | false | Independent module control; restart after editing. |
| Diagnostics | DebugMode | false | Shared troubleshooting switch. |

HeimKit creates `heimkit.cfg` in BepInEx's configuration directory and adds missing settings while preserving existing values. WeatherGuardSettings.cs binds Enabled and obtains the shared DebugMode setting through DiagnosticsSettings before module initialization.

## Eligible pieces

A candidate must have Piece and WearNTear components, a nonzero persistent creator, positive finite health, persistent world data, a static position and rain/wetness wear. Its category must be BuildingWorkbench, BuildingStonecutter or DeepNorth. Containers, moving objects, non-building categories and materials already immune to rain are excluded.

Persistent creator and platform attribution are never assigned or transferred. Before acquisition, the server instance's cached Piece creator values are refreshed from its existing world data.

## Server control and ownership

Every second, the module gathers nearby and distant world records around ready connected players whose character object belongs to their session. Overlapping records are deduplicated, with nearby records taking precedence. The module extends ordinary server instance loading and retention to those regions. Terrain is loaded through vanilla zone handling at intervals of at least 0.1 seconds. The server reference position is unchanged.

A piece is acquired only when its server instance, its sector and eight neighboring terrain sectors, and surrounding near-sector instances are ready. Readiness is cached per sector for each scan. Its network owner then becomes the server through native ownership replication and force-send. This changes temporary network ownership, not the creator.

Newly spawned instances in retained active regions are kept between scans so ordinary drops and fragments are not prematurely removed. Pieces leaving retained nearby regions, losing ready context or becoming ineligible are released to owner zero for vanilla reassignment.

## Wear and native commands

During a protected server WearNTear.UpdateWear invocation, `m_noRoofWear` is temporarily false. A Harmony finalizer restores the original value, including on exceptions. The outside-active-area early return is bypassed only within the protected wear invocation, allowing native support calculations to run. UpdateWear itself is not skipped.

The flag suppresses rain and underwater wetness wear. Snow, Ashlands/lava and other biome hazards remain outside this protection. Combat, repair, removal, support collapse and resource drops use native code rather than periodic healing or health-delta inference. Their behavior during ownership handover requires live verification.

## Client-update guards

Incoming connected-client state records and sector invalidations for protected IDs are excluded from ZDO packets. Unprotected records retain their original content. Packet parsing preserves the original cursor and validates the complete packet before applying metadata corrections.

Rejected client revisions at or above the server's revisions cause the server to advance its metadata and resend its own snapshot. Client health, support and creator values are not accepted. Revision overflow disables the module rather than wrapping.

Client health, snow and fragment broadcasts for protected pieces are rejected. Protected IDs are removed from stale client destruction lists before forwarding, and their server snapshots are resent. Native owner commands captured with an old owner target are routed to the server. Server-generated state and destruction messages remain unchanged. Unrelated routed messages bypass the protection payload decoder; ready connections with forged sender IDs are rejected for relevant messages.

## Bounds and failure handling

| Limit | Value |
| --- | --- |
| Distinct character-centered regions | 8 |
| Retained nearby/distant context records | 32,768 |
| Protected pieces | 8,192 |
| Filtered packet size | 2 MiB |
| Packet record count | 32,768 |

Capacity warnings state that unmanaged pieces remain under vanilla processing. Context loading and ownership handover may delay protection. Loading additional terrain and network objects increases server work.

Disabling, shutdown or module failure deactivates patches and releases network ownership. Other modules remain independent. The implementation does not persist a weather flag, claim marker or new creator. It does not automatically remove custom fields already stored in a world by another implementation.

## Logging

Standard logs report configuration, enabled/disabled state, readiness, first acquisition count, capacity warnings and errors. DebugMode adds up to six acquisition samples per 60 seconds and one status summary per 60 seconds.

Summaries contain protected/eligible counts, terrain/instance/context waits, region/object counts, acquisition/release totals, blocked client records and broadcasts, prevented ownership transfers, routed owner commands and protected UpdateWear invocation counts. Invocation counts and readiness logs do not establish successful rain prevention. Persistent creator and platform account IDs are not logged.

## Source responsibilities

| File in `src/HeimKit/Features/WeatherGuard/` | Responsibility |
| --- | --- |
| WeatherGuard.cs | Settings construction, compatibility, module lifecycle and failure isolation. |
| WeatherGuardSettings.cs | Feature configuration bindings and defaults. |
| WeatherGuardController.cs | Region loading, readiness, acquisition, release and operational counters. |
| WeatherGuardDiagnostics.cs | Debug summaries, acquisition sample formatting, logging limits and reset. |
| WeatherGuardPatches.cs | Harmony hooks for processing, ownership and networking. |
| WeatherGuardRules.cs | Eligibility, acquisition gates, routing and revision rules. |
| WeatherGuardNetworkFilter.cs | Selective packet filtering and cursor preservation. |
| Models/WeatherGuardStatus.cs | Processing-counter snapshot supplied to diagnostics. |
| Models/Routing.cs | Routing decision enum used by rules and patches. |

User installation, testing and troubleshooting instructions belong in [README.md](../../README.md). Project design belongs in [architecture.md](../architecture.md); file placement and development procedures belong in [contributing.md](../contributing.md).

ConfigurationLoader manages the shared file, and DiagnosticsSettings binds the common debug switch. The main class activates the controller only after enable and compatibility checks pass and world services are ready; the controller performs gameplay work and reports processing failures to the main class.
