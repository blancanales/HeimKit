# WeatherGuard specification

## Purpose and scope

WeatherGuard automatically protects eligible player-built structural pieces from rain and wetness wear on a Windows Valheim dedicated server. Existing creator ownership is preserved. Activation requires no player repair, logout, rejoin or manual refresh. Unclaimed world-generated pieces are not protected or claimed by the current code.

The module uses BepInEx and Harmony and is designed for unmodified clients. Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

## Configuration

Automatic Debug output follows the shared [five-minute empty-server pause](../architecture.md#empty-server-debug-pause). Reconnection resumes eligible diagnostics without restarting the feature; warnings, errors, requested reports and gameplay remain available.

The shared [hidden debug suppression contract](../server-setup.md#hidden-per-feature-debug-suppression) applies in this feature's existing configuration section. Settings resolve it through DebugLoggingResolver; effective debug policy gates optional output and diagnostic collection while gameplay and standard lifecycle/warnings/errors remain active. The key has no generated default.

| Section | Setting | Default | Behavior |
| --- | --- | --- | --- |
| WeatherGuard | Enabled | false | Independent module control; restart after editing. |
| WeatherGuard | ProtectRain | true | Suppress qualifying rain wear; restart after editing. |
| WeatherGuard | ProtectUnderwater | true | Suppress qualifying underwater wetness wear; restart after editing. |
| WeatherGuard | ExcludedPrefabs | Empty | Comma-separated exact case-sensitive prefab identifiers; restart after editing. |
| Diagnostics | DebugMode | false | Enable diagnostics across features unless suppressed. |
| WeatherGuard | DebugMode | false | Enable only this feature’s diagnostics when global debug is off; hidden suppression takes precedence. |
| WeatherGuard | DebugLogLevel | Medium | Only Medium and Detailed are accepted; invalid values fall back to Medium. Medium reports compact protection status; Detailed enables wear, health, loading and ownership evidence. |
| WeatherGuard | LogWorldCreatedItems | false | Include otherwise qualifying world-created structures in debug output; never enables their protection. |

HeimKit creates `heimkit.cfg` in BepInEx's configuration directory and adds missing settings while preserving existing values. WeatherGuardSettings.cs binds Enabled and binds its own DebugMode and obtains global DebugMode through DiagnosticsSettings before module initialization.

## Eligible pieces

A candidate must have Piece and WearNTear components, a nonzero persistent creator, positive finite health, persistent world data, a static position and rain/wetness wear. Its category must be BuildingWorkbench, BuildingStonecutter or DeepNorth. Containers, moving objects, non-building categories and materials already immune to rain are excluded.

Configured prefab exclusions do not broaden native eligibility or assign creators. Native structural/material/health exclusions take priority; otherwise an exact configured prefab match takes priority over channel disablement and creator eligibility. With both weather channels disabled, otherwise qualifying pieces receive `weather_protection_disabled` and are not acquired. World-created pieces remain excluded regardless of the channel settings.

Excluded-prefab entries are trimmed and deduplicated with ordinal, case-sensitive matching against the registered prefab's name, not an instance name or localized display name. Only ASCII letters, digits and underscores are accepted; patterns, separators other than commas, empty list entries and entries beyond 1,024 distinct names are ignored with one construction warning containing a rejected count. An entirely empty/whitespace value means no exclusions. Syntactically valid names absent from the current prefab registry remain exact names and produce one installation warning with a count and at most six samples. Unknown names do not match other prefabs. Excluded pieces retain native processing, while their colliders and support dependencies can still be loaded for neighboring protected pieces.

Persistent creator and platform attribution are never assigned or transferred. Before acquisition, the server instance's cached Piece creator values are refreshed from its existing world data.

## Server control and ownership

Every second, the module gathers nearby and distant world records around ready connected players whose character object belongs to their session. Overlapping records are deduplicated, with nearby records taking precedence. The module extends ordinary server instance loading and retention to those regions. Terrain is loaded through vanilla zone handling at intervals of at least 0.1 seconds. The server reference position is unchanged.

A piece is acquired only when its server instance, its sector and eight neighboring terrain sectors, and surrounding near-sector physics/wear/shield instances are ready. Collider-bearing prefabs remain dependencies, including non-building objects; collider-free sounds/effects without wear or shield components do not block processing. Readiness is cached per sector for each scan, and dependency classification is cached per prefab. Its network owner then becomes the server through native ownership replication and force-send. This changes temporary network ownership, not the creator.

Newly spawned instances in retained active regions are kept between scans so ordinary drops and fragments are not prematurely removed. Pieces leaving retained nearby regions, losing ready context or becoming ineligible are released to owner zero for vanilla reassignment, with a forced state send. Each scan checks actual ownership of retained protected pieces and reasserts the server after an observed ownership loss. This observation alone does not identify the writer.

With no ready players while still occupied, a scope refresh releases pieces outside player-centered regions. On empty or unavailable presence, disposal immediately releases retained ownership, clears regions and removes gameplay hooks. Native systems continue without WeatherGuard's protection controller. This is not whole-world offline protection.

## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects its shared PlayerServices into WeatherGuard; the controller selects ready players from its cached presence snapshot without a second population census. The enabled, compatible module owns one Events subscription and one optional WeatherGuardController. True occupancy permits controller installation only when ZNet is a dedicated server and ZDOMan, ZNetScene, ZoneSystem and ZRoutedRpc are ready. Tick retries delayed readiness and disposes controllers on readiness loss or world replacement. Additional logins do not recreate the controller. False/null disposes the controller and its hooks, releases pieces to vanilla ownership, and retains the subscription for reconnection. Shutdown, rollback or failure disposes both resources; failure disables only WeatherGuard. No AFK activity lease is needed.

Coverage requests are an explicit administration/status exception: the independent request-file handler polls even while gameplay is paused or the module is disabled. Reports identify `server_empty`, `player_presence_unavailable`, `waiting_for_world` or `module_inactive` when no controller can report coverage. This operator trigger does not keep protection hooks or scanning active.

## Wear and native commands

The installed game calculates wear only on the current network owner and after its normal loading warmup (normally 30 seconds). Its periodic ownership assignment can try to transfer server-owned pieces near players back to their clients. WeatherGuard retains server ownership through SetOwner guards and rejects protected client packet records, including the SetOwnerInternal path used by incoming packets. Client commands target the owner known at invocation time; stale nonbroadcast targets are redirected to the server.

A Harmony transpiler gates only the weather-damage branch of WearNTear.UpdateWear for protected, server-owned instances. It preserves the preceding rain-wetness calculation and never changes `m_noRoofWear`, retaining native wetness visuals and fire handling. Installation rejects an unexpected flag-read/branch shape rather than silently applying a different patch. The outside-active-area early return is bypassed only within the protected wear invocation; the finalizer clears that scope even on exceptions. UpdateWear, its warmup and native support calculations are not skipped.

The gate independently suppresses rain and underwater wetness damage according to ProtectRain and ProtectUnderwater. With both enabled it retains the existing blanket weather-branch bypass. For partial protection it reads the native `m_rainWet` value already calculated earlier in UpdateWear and native IsUnderWater. It allows the native branch when any active wetness source is unprotected, including simultaneous rain/underwater exposure; it suppresses the branch only when all active wetness sources are protected. No sources during partial protection leave native processing unchanged. The native weather flag remains a prerequisite, and shield, health, timers and final damage restrictions remain native. Both channels disabled leave all pieces unacquired and the installed hooks inactive. No weather/roof flag or native timer is assigned by this selection. Snow, Ashlands/lava and other biome hazards remain outside this protection. Combat, repair, removal, support collapse and resource drops use native code rather than periodic healing or health-delta inference.

## Native rain timing and source

These timing and damage values come from Valheim's own compiled dedicated-server assembly, not from HeimKit settings or an online timing estimate. The source is `valheim_server_Data/Managed/assembly_valheim.dll` in the local server installation matching [Supported environment](../server-setup.md#supported-environment). Inspect it with the [ILSpy decompiler](https://github.com/icsharpcode/ILSpy), using its command-line tool `ilspycmd` or desktop application. Decompiled C# is a readable reconstruction of the compiled assembly; the assembly remains the underlying source of evidence. Proprietary game code and assemblies are not distributed with HeimKit.

| Value or condition | Valheim method and calculation |
| --- | --- |
| Normal loading warmup | `WearNTear.ShouldUpdate(float time)` normally permits wear when `time - m_createTime > 30f`; negative creation time and pre-snow handling can bypass this wait. |
| Rain exposure | `WearNTear.UpdateWear(float time)` sets rain wetness when the piece has native weather wear, no cached roof, no shield and a wet environment. `IsWet()` also includes underwater wetness. |
| First wet interval | `WearNTear.UpdateWear(float time)` sets `m_rainTimer = time` on the first qualifying wet update. A later update contributes weather damage when `time - m_rainTimer > 60f`. Exactly 60 seconds does not satisfy that comparison. |
| Weather-damage amount | The same method contributes `5f` to percentage wear and converts percentage wear to health damage using `num / 100f * m_health`: five percent of maximum health from the weather branch. Other native branches can contribute to the same wear total. |
| Subsequent intervals | After a weather-damage contribution, the timer is set to the current wear time. A dry update inside the weather branch resets it to zero. The branch is evaluated only while the native health fraction is above `0.5f` and the piece is unshielded. |
| Final damage restrictions | `WearNTear.UpdateWear` cancels percentage wear for a piece that cannot be removed and suppresses its application when `NoBuildingFall` is active. |
| Roof-cache refresh | `WearNTear.UpdateCover(float dt)` refreshes cover after its accumulated timer exceeds `4f`, when the environment is wet or the piece is in Deep North. `WearNTearUpdater.UpdateWearNTear` schedules these calls; this accumulator threshold must not be treated as a guaranteed four-second wall-clock delay. |

The first rain-damage opportunity occurs on the first qualifying wear update after the wet timer exceeds 60 game-time seconds, following any loading and cover-detection delay. Periodic update scheduling adds delay. Therefore there is no unconditional fixed duration measured from player spawn; elapsed time alone does not establish qualifying exposure.

WeatherGuard bypasses the weather-damage branch for protected server-owned pieces, so their native rain timer does not advance through that branch. Its logged continuous wet duration is a separate diagnostic observation of qualifying protected gate entries, not the native rain timer or a calculated amount of prevented damage. Snow, Ashlands and lava have separate native conditions and timers; the rain interval does not apply to every environmental hazard.

## Client-update guards

Incoming connected-client state records and sector invalidations for protected IDs are excluded from ZDO packets. Unprotected records retain their original content. Packet parsing preserves the original cursor and validates the complete packet before applying metadata corrections. Embedded ZDO and relevant routed-packet lengths must fit the remaining bytes and packet-size limit before allocating a payload buffer; trailing routed data is rejected. Unrelated routed traffic retains native decoding.

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

Disabling, shutdown or module failure deactivates patches and releases network ownership, forcing released snapshots to connected peers in the same world. Cleanup attempts every retained piece even if one release fails, reports failures and clears local tracking. A replacement world is never used to send old ownership snapshots. Other modules remain independent. The implementation does not persist a weather flag, claim marker or new creator. It does not automatically remove custom fields already stored in a world by another implementation.

## Requested coverage

WeatherGuardCoverageRequests handles the fixed local request file in the loader config directory at most once per second. The [admin access procedure](../server-setup.md#requested-weatherguard-coverage) is filesystem-authorized and requires no client changes or debug enablement. The file is consumed before producing a single ordinary requested SUMMARY. Access warnings are bounded until successful consumption; report exceptions are isolated from gameplay processing. The plugin ticks inactive modules for explicit administration, while each module continues gating gameplay by IsActive. WeatherGuard reports inactive/unobserved scope when disabled or awaiting world services rather than performing a whole-world scan.

WeatherGuardController builds coverage data only on request from the retained nearby record set and the most recent player-region scan. Eligibility and exact exclusions use the same selection as acquisition; loaded-instance validity, native current owner, terrain and cached surrounding-context readiness establish protected counts. Scan age and capacity limitations are explicit. CoverageSnapshot partitions observed records into eligible/skipped, and eligible records into protected/waiting; unloaded eligible records are a waiting subset. Skipped reasons include records that are not structural candidates. Distant records and world areas outside retained scope are not evaluated. Unknown counts are distinct from measured zeros. Rain/underwater configuration describes channels, not proven prevented damage. WeatherGuardDiagnostics formats requested reports independently of automatic debug gating and budgets.

## Logging

All messages follow the [shared logging contract](../architecture.md#logging). Info reports configuration loaded/created, enabled/disabled state and readiness in concise messages. Warning reports actionable capacity or compatibility problems concisely; Debug carries initial processing scope and settings, while Error and exception details retain structured evidence. Enabled and readiness messages do not assert rain protection.

The shared [repetition and volume limits](../architecture.md#repetition-and-volume-limits) govern all streams. Optional diagnostics use BepInEx Debug severity; native exceptions retain Error severity, and explicitly requested coverage reports retain Info. Global OR WeatherGuard DebugMode enables diagnostics; the hidden suppression override takes precedence. With `DebugLogLevel=Medium`, diagnostics report one SUMMARY and up to twelve compact item RESULT records per 60 seconds. Item records say “Item protected” or “Item not protected” and include object ID, prefab, position, `protected`, channel-specific `rain_protected`/`underwater_protected`, origin and reason. Protected means server-managed for the configured channels, not that both channels are necessarily enabled. Scope is nearby structures passing all eligibility checks other than creator: eligible player-built items, plus world-created items only when `LogWorldCreatedItems=true`. Containers, moving objects, non-building items and weather-immune materials are omitted. Summary counts describe this qualifying scope; unprotected player items have loading, capacity or acquisition reasons, while included world items retain `world-created` exclusion.

New items and changed states not yet reported take priority. Unchanged items are not repeatedly rotated into the log; use the shared compact-heartbeat rule. Deferred changes are counted in `pending_changed_samples_now`; samples are bounded and do not promise every object immediately. State memory is limited to the current retained qualifying set. Medium does not gather detailed rain histories or diagnostic roof probes. Startup, readiness, warnings and errors remain visible independently of these choices. Level and world-inclusion changes require restart.

With `DebugLogLevel=Detailed`, DebugMode permits at most one changed-state SUMMARY per 60 seconds under the shared repetition rules: eligible/managed/current waiting counts, ready-player/character waits, watched player/control counts, interval acquisitions/releases/wet gate entries, exclusions and omitted-message counts. A reserved DETAIL supplies interval processing/support/native-command/network counters, lifetime acquisition/release/gate totals and cumulative release reasons. Deliberate releases and observed unexpected owner changes remain separate measurements.

Watched RESULT records present protection, health outcome and WeatherGuard action together. `protection` is `active`, `waiting` or `excluded`, with `protection_reason` identifying eligibility or the current loading/capacity blocker. `health_change`, `health_before`, `health_after` and `health_lost_interval` describe measurements since the previous report (or tracking start for the first report). Loss measurements accumulate each observed decrease, retaining damage followed by repair between reports; they do not reconstruct changes between observations. `weather_damage=unconfirmed` and `damage_source` prevent health loss from being presented as a known weather cause.

`action=weather_branch_bypassed` requires an actual bypass observed during the reporting interval, with interval and lifetime bypass counts. `left_to_native_owner` means WeatherGuard left the piece unmanaged; it does not assert that an unobserved client calculation ran. `weather_bypass_not_observed` distinguishes managed pieces without an observed bypass. The action describes the interval, while protection and blockers describe current state. `weather_opportunity` distinguishes unavailable owner calculations, unmet conditions, insufficient qualifying duration and qualifying duration exceeding the native interval; duration alone does not establish an actual prevented damage amount. SUMMARY includes watched pieces with observed health loss and watched bypass counts for the reporting interval. These counts cover the bounded watched sample, not every world piece.

At most six watched RESULT records accompany a summary: three exposed eligible pieces and, when `LogWorldCreatedItems=true`, three exposed world-created controls. They contain object identity, observation source, tracked duration, health first/current/minimum/delta, native maximum health, observed continuous/longest qualifying wet duration, gate counts and current blockers. Health loss remains prominent even after rain ends. World-created controls can expose replicated health changes, but their client wet timers are unavailable and are logged as `unknown`, not zero. `server_owned_state` identifies authoritative server-side state; gate counts separately establish calculation entry. No health delta establishes its cause or client presentation.

Dry gates, observed owner or management changes and game-clock gaps greater than ten seconds reset continuous wet duration. The duration is sampling evidence, not a replay of vanilla rain damage or a measurement of prevented damage. No native rain timer, creator or roof cache is changed by diagnostics. Warmup blockers include the remaining native game-time allowance; scheduling can add delay after that allowance reaches zero.

A rotating debug probe inspects fresh native roof geometry for at most twelve structural candidates per scan. Eligible candidates first observed after an earlier scan can replace one eligible history once exposed. Later observation can result from building, movement or delayed data; it does not prove creation time. Cached/fresh roof disagreements include cover timer and roof-probe age so older geometry observations are not mistaken for a current calculation. Roofed objects do not prove rain protection; underwater wetness can qualify separately. Shielding, health threshold, removability and NoBuildingFall are explicit evaluation blockers.

OBJECT records identify first observations, changed exclusions and tracking termination with an observed reason. Unchanged exclusions contribute counts without repeating individual messages. ACTION records identify acquisition/release/owner operations. DETAIL records explain missing terrain or prefab-backed dependencies, processing warmup, native timing, environment and calculation entry. Numeric owners, instance-loading classification and timing caches are supporting details rather than repeated routine result fields. World-data health that cannot be distinguished from a missing or NaN field is explicitly labeled `unknown_or_nan`, rather than a fabricated negative health value.

OBJECT, ACTION and additional DETAIL output each allow six messages per 60 seconds. Wear DETAIL selection additionally allows at most three distinct candidates per protected/unprotected and exposed/covered group. Native command receivers have a separate six-RESULT allowance with command, processor, health before/after and object availability. Errors retain error severity. Diagnostics caps affect printing only; watched health is collected every scan and after native wear and command execution, and operational counters continue independently. Memory for exclusion observations is restricted to retained records. Persistent creator/account IDs are not logged; creator changes are reported as a boolean.

Server environment can differ from client environment. Client-side wear calculations, delivery acknowledgements and client presentation cannot be directly observed with unmodified clients. RESULT messages retain that boundary rather than asserting success from compilation, elapsed wall-clock time or unchanged health alone.

## Source responsibilities

| File in `src/HeimKit/Features/WeatherGuard/` | Responsibility |
| --- | --- |
| WeatherGuard.cs | Settings construction, compatibility, module lifecycle and failure isolation. |
| WeatherGuardSettings.cs | Feature configuration bindings and defaults. |
| WeatherGuardDebugLogLevelValues.cs | BepInEx validation of the two supported diagnostic levels. |
| WeatherGuardController.cs | Region loading, readiness, acquisition/release, ownership reconciliation, native evidence collection and operational counters. |
| WeatherGuardDiagnostics.cs | Debug gating, summaries/evidence formatting, independent sample limits and reset. |
| WeatherGuardPatches.cs | Harmony hooks for processing, ownership and networking. |
| WeatherGuardCoverageRequests.cs | Local one-shot admin request consumption, polling bounds and failure isolation. |
| Models/CoverageSnapshot.cs | Requested coverage counts, scope and reason measurements. |
| WeatherGuardRules.cs | Eligibility, acquisition gates, routing and revision rules. |
| WeatherGuardNetworkFilter.cs | Selective packet filtering and cursor preservation. |
| Models/WeatherGuardStatus.cs | Processing-counter snapshot supplied to diagnostics. |
| Models/DebugLogLevel.cs | Medium/Detailed diagnostic level selection. |
| Models/ProtectionObservation.cs | Qualifying-item protection snapshot for compact diagnostics. |
| Models/WearObservation.cs | Bounded native wear/command evidence supplied to diagnostics. |
| Models/RainEvidence.cs | Bounded per-object health, ownership and wet-processing history. |
| Models/WearProcessingState.cs | Invocation scope and diagnostic observation restored by the wear finalizer. |
| Models/Routing.cs | Routing decision enum used by rules and patches. |

User installation and general configuration belong in [server setup](../server-setup.md); feature usage and troubleshooting belong in the [feature guide](../features/weather-guard.md). Project design belongs in [architecture.md](../architecture.md); file placement and development procedures belong in [contributing.md](../contributing.md).

ConfigurationLoader manages the shared file, and DiagnosticsSettings binds the common debug switch. The main class activates the controller only after enable and compatibility checks pass and world services are ready; the controller performs gameplay work and reports processing failures to the main class.
