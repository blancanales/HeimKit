# WeatherGuard specification

## Purpose and scope

WeatherGuard automatically protects eligible player-built structural pieces from rain and wetness wear on a Windows Valheim dedicated server. Existing creator ownership is preserved. Activation requires no player repair, logout, rejoin or manual refresh. Unclaimed world-generated pieces are not protected or claimed by the current code.

The module uses BepInEx and Harmony and is designed for unmodified clients. Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

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

Newly spawned instances in retained active regions are kept between scans so ordinary drops and fragments are not prematurely removed. Pieces leaving retained nearby regions, losing ready context or becoming ineligible are released to owner zero for vanilla reassignment, with a forced state send. Each scan checks actual ownership of retained protected pieces and reasserts the server after an observed ownership loss. This observation alone does not identify the writer.

## Wear and native commands

The installed game calculates wear only on the current network owner and after its normal loading warmup (normally 30 seconds). Its periodic ownership assignment can try to transfer server-owned pieces near players back to their clients. WeatherGuard retains server ownership through SetOwner guards and rejects protected client packet records, including the SetOwnerInternal path used by incoming packets. Client commands target the owner known at invocation time; stale nonbroadcast targets are redirected to the server.

A Harmony transpiler gates only the weather-damage branch of WearNTear.UpdateWear for protected, server-owned instances. It preserves the preceding rain-wetness calculation and never changes `m_noRoofWear`, retaining native wetness visuals and fire handling. Installation rejects an unexpected flag-read/branch shape rather than silently applying a different patch. The outside-active-area early return is bypassed only within the protected wear invocation; the finalizer clears that scope even on exceptions. UpdateWear, its warmup and native support calculations are not skipped.

The gate suppresses rain and underwater wetness damage. Snow, Ashlands/lava and other biome hazards remain outside this protection. Combat, repair, removal, support collapse and resource drops use native code rather than periodic healing or health-delta inference.

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

Standard logs report configuration, enabled/disabled state, readiness, first acquisition count, capacity warnings and errors. DebugMode adds one two-line summary per 60 seconds. Acquisition, eligibility, context and ownership streams each have an independent limit of six detailed samples per 60 seconds. Native damage/repair/removal receiver samples have a separate limit of six. Wear evidence is limited to three distinct protected pieces and three distinct unprotected structural controls per 60 seconds; one object cannot consume all wear samples in a window. Samples contain object IDs, prefab names and positions to identify the object. Persistent creator and platform account IDs are not logged.

Summaries distinguish current-scan candidate, exclusion, release-reason and ready-character counts from controller-lifetime transition/guard counters. Actual entry into the protected weather gate and completed native support passes are counted separately from UpdateWear invocations. With DebugMode enabled, wet opportunities count gate entries with native wetness, health above half and no shield; they do not measure a continuous 60-second wet interval or prove client delivery. Gate evidence can remain zero during warmup even when invocation counts increase.

Context samples identify the first missing terrain sector or prefab-backed surrounding instance, retention membership, zone generation and server terrain readiness. Ownership samples show previous/requested/current owner, releases with reasons and observed loss followed by reassertion. Native receiver samples give command name, owner and health before/after; receiving a command does not guarantee a health change or observed resource drops.

Wear samples record owner, eligibility, warmup, actual gate entry/suppression, server environment/reference position, cached roof, shield, underwater state, cached health fraction, maximum health, rain timer, removability and health before/after. NoBuildingFall and removability are reported because vanilla suppresses wear damage for that modifier or a nonremovable piece, even for controls. These are server observations; unowned controls run their wear on clients. Client weather and wear timers are not available with unmodified clients, and server weather at its reference position can differ from player weather. Health changes alone never establish their cause.

## Source responsibilities

| File in `src/HeimKit/Features/WeatherGuard/` | Responsibility |
| --- | --- |
| WeatherGuard.cs | Settings construction, compatibility, module lifecycle and failure isolation. |
| WeatherGuardSettings.cs | Feature configuration bindings and defaults. |
| WeatherGuardController.cs | Region loading, readiness, acquisition/release, ownership reconciliation, native evidence collection and operational counters. |
| WeatherGuardDiagnostics.cs | Debug gating, summaries/evidence formatting, independent sample limits and reset. |
| WeatherGuardPatches.cs | Harmony hooks for processing, ownership and networking. |
| WeatherGuardRules.cs | Eligibility, acquisition gates, routing and revision rules. |
| WeatherGuardNetworkFilter.cs | Selective packet filtering and cursor preservation. |
| Models/WeatherGuardStatus.cs | Processing-counter snapshot supplied to diagnostics. |
| Models/WearObservation.cs | Bounded native wear/command evidence supplied to diagnostics. |
| Models/WearProcessingState.cs | Invocation scope and diagnostic observation restored by the wear finalizer. |
| Models/Routing.cs | Routing decision enum used by rules and patches. |

User installation and general configuration belong in [server setup](../server-setup.md); feature usage and troubleshooting belong in the [feature guide](../features/weather-guard.md). Project design belongs in [architecture.md](../architecture.md); file placement and development procedures belong in [contributing.md](../contributing.md).

ConfigurationLoader manages the shared file, and DiagnosticsSettings binds the common debug switch. The main class activates the controller only after enable and compatibility checks pass and world services are ready; the controller performs gameplay work and reports processing failures to the main class.
