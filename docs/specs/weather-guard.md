# WeatherGuard specification

## Purpose and scope

WeatherGuard automatically protects eligible player-built structural pieces from rain and wetness wear on a Windows Valheim dedicated server. Existing creator ownership is preserved. Activation requires no player repair, logout, rejoin or manual refresh. Unclaimed world-generated pieces are not protected or claimed by the current code.

The module uses BepInEx and Harmony and is designed for unmodified clients. Supported game and loader versions are documented in [server setup](../server-setup.md#supported-environment).

## Configuration

Automatic Debug output follows the shared [five-minute empty-server pause](../architecture.md#empty-server-debug-pause). Reconnection resumes eligible diagnostics without restarting the feature; warnings, errors, requested reports and gameplay remain available.

The shared [hidden debug suppression contract](../server-setup.md#hidden-per-feature-debug-suppression) applies in this feature's existing configuration section. Settings resolve it through DebugLoggingResolver; effective debug policy gates optional output and diagnostic collection while gameplay and standard lifecycle/warnings/errors remain active. The key has no generated default.

| Section | Setting | Default | Behavior |
| --- | --- | --- | --- |
| WeatherGuard | Enabled | true | Independent module control; restart after editing. |
| WeatherGuard | ProtectRain | true | Suppress qualifying rain wear; restart after editing. |
| WeatherGuard | ProtectUnderwater | true | Suppress qualifying underwater wetness wear; restart after editing. |
| WeatherGuard | ExcludedPrefabs | Empty | Comma-separated exact case-sensitive prefab identifiers; restart after editing. |
| Diagnostics | DebugMode | false | Enable diagnostics across features unless suppressed. |
| WeatherGuard | DebugMode | false | Enable only this feature’s diagnostics when global debug is off; hidden suppression takes precedence. |
| WeatherGuard | DebugLogLevel | Detailed | Only Medium and Detailed are accepted; invalid values fall back to Detailed. Medium reports the grouped prevention path; Detailed adds native-condition samples, health anomalies, loading and ownership evidence to the prevention path. |
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

A guarded Harmony transpiler transforms only the native five-percent weather-damage addition in WearNTear.UpdateWear, after its warmup, native weather-wear flag, shield, health, wetness and greater-than-sixty-second wet-timer checks. For protected server-owned pieces, configured channel selection replaces that weather contribution with zero. Installation validates the flag reads, preceding native wetness assignment, timer update, sixty-second threshold and five-percent addition before native support calculation. Unexpected shapes disable the module rather than patching another branch. All original native instructions remain, including timer start, advance/reset, wet visuals, support, snow and other hazards, final damage restrictions and damage application. The outside-active-area return is bypassed only within protected wear; a finalizer clears the scope on success or error.

ProtectRain and ProtectUnderwater independently select suppression. Native m_rainWet and IsUnderWater identify active sources at the contribution point. If any active source is unprotected, native weather damage remains. With both enabled, a qualifying native weather contribution is suppressed regardless of source. Both disabled leave pieces unacquired. No roof, weather flag, native wetness or timer value is assigned by HeimKit; native evaluation advances its own timer. Combat, repair, removal, support collapse and drops remain native rather than periodic healing. Snow, Ashlands/lava and other hazards remain outside this protection.

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

WeatherGuard allows the native wet timer to run and suppresses the weather contribution only when the native method reaches its addition. It does not estimate prevented damage from a shadow wet duration or from unchanged health. Native server wetness can differ from a remote player's local weather; biome context and the server wet flag are logged separately.

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

The shared [repetition and volume limits](../architecture.md#repetition-and-volume-limits) govern all streams. Optional diagnostics use Debug severity and the live global-OR-feature policy with hidden suppression. Warnings, errors and explicit coverage requests retain their independent severity and availability.

With Detailed debugging enabled, module initialization emits one `DETAIL | Settings and processing limits` record containing channel switches, exclusions, diagnostic level, world-control inclusion, capacity limits, roof-probe bounds, native weather threshold/amount and scope/source definitions. This record belongs to module startup, not controller recreation after occupancy or world transitions. Medium omits the settings detail; Debug-off and hidden suppression omit it. The shared startup debug-policy declaration remains independently governed by its existing contract. Recurring summaries and piece samples contain observed conditions, counts and results, without repeating configured switches or fixed processing limits. Explicitly requested coverage reports retain their self-contained channel context.

Medium reports one grouped prevention-path SUMMARY per sixty seconds: eligible and managed pieces, ownership/context waits, native wear calls, blocking-stage counts, native weather contributions due, contributions removed and confirmed prevention counts. Stages distinguish native warmup, shielding, health at or below half, native cached roof coverage, server-environment dryness, wet-timer start, wet-timer wait and conditions met. Counts describe calls rather than distinct pieces; native due/removal/prevention counters establish actual progress. Geometry counts and biome context accompany this path, with rolling-probe ages and unknown counts. Medium does not gather individual health histories or detailed samples.

Detailed includes the Medium path plus loading, exclusions, capacity and omitted-message evidence. One grouped DETAIL adds acquisition/release interval counts with interval release reasons, ownership, networking, native-command and structural-support counters. Wear-call and native-weather-due interval counts appear only in the path SUMMARY, while their useful lifetime totals remain separately labeled. Eligibility is reported once for the full current scope. Representative wear DETAIL shows native roof/wetness/shield/health values, warmup, native timer start, current game time and final restrictions. Identical blocking phases do not repeat separately for every piece. Changed phases or native errors remain useful diagnostic evidence. Object/loading streams retain bounded samples; successful acquisition/release evidence is grouped into interval counts and release reasons rather than repeated per-piece entries; routine rejected owner transfers are counted rather than repeatedly printed.

Up to three exposed player-built pieces and optional world-created controls retain sampled health histories. Individual RESULT entries are emitted for newly observed health loss, including loss followed by repair. Unchanged health produces no routine per-piece result; suppression counts belong in grouped summaries and the normal prevention notice. Health loss retains its measured amount and identity without attributing an unknown cause to weather. Native command exceptions remain Error. Diagnostic collection does not alter roof, wetness, timers, creator or gameplay decisions.

Normal output for prevention is tied to one complete native WearNTearUpdater wear sweep. UpdateWearNTear processes the scheduled pieces across multiple frame calls, retaining a nonzero m_index while more remain and returning it to zero when the sweep finishes. Confirmed pieces accumulate across those calls and are reported only at that completed-sweep boundary. The updater index API is checked during installation; partial sweeps emit no result. Failure, pause, shutdown and world replacement discard unfinished sweep data. A successful protected UpdateWear invocation records its distinct piece only after a configured native weather contribution was removed and final native removability/NoBuildingFall restrictions would permit damage. On successful sweep completion, emit one Info entry: `WeatherGuard: Prevented weather damage to N pieces.` For exactly one piece, identify its captured registered prefab name and object ID instead: `WeatherGuard: Prevented weather damage to wood_stair (piece 1:341004).` If the prefab is unavailable, retain the known piece ID. Multiple pieces emit the count only, without separate per-piece entries. Player-built eligibility remains a documented scope rule and is not repeated in this Info wording. Piece identity is captured before native wear executes, so successful native processing can still report it if the object is removed by another native wear cause. Duplicate identities within the sweep count once; empty, unsuccessful or failed batches emit no successful result. A direct native wear invocation outside the updater is its own single-piece pass. A later genuine weather-damage opportunity can report prevention again. This event reports removal of the native weather contribution, never prevention of support/combat damage, preservation of the entire piece or a client-visible health change. Ownership acquisition, exposed geometry and stable health alone cannot emit it.

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

Gameplay lifecycle uses the shared joined-world state, established by native character spawning rather than an incoming connection or login handshake. After its own cleanup completes, the module acknowledges an empty-server pause to the shared service. One shared PlayerPresence notice confirms all features are inactive until a player joins; individual empty-server notices are suppressed. After a player joins, the shared active notice waits for successful initialization of all enabled consumers. Each enabled module reports Ready once when its required dedicated-server services become available, regardless of players. This indicates readiness to process, not installed player-dependent hooks or a verified gameplay effect. Individual processing activation and pause reasons belong in Debug, including later starts; no per-feature Resumed notice appears in normal output. Death/respawn does not end a joined session. See the [shared lifecycle](../architecture.md#presence-driven-lifecycle).

Normal prevention output follows the native weather-contribution/pass contract in [Logging](#logging). Acquisition and geometry checks are Debug evidence only.

Processing capacity degradation produces one Warning when the limit is reached, not one warning per unmanaged piece or poll. WeatherGuardDiagnostics.CapacityState reports one Info recovery when a later scope scan clears that limit. Recovery states only that the processing limit cleared; it does not prove full-world protection or weather-damage prevention. Exclusion-list setup warnings include the rejected/unknown count and the setting to review. Individual acquisition and per-piece evidence remain optional Debug output; requested coverage reports remain separately available. See the [standard logging contract](../architecture.md#standard-feature-events-and-grouping).

Debug policy startup declarations follow the [shared contract](../architecture.md#debug-policy-startup-declaration), identifying global/feature enablement and hidden suppression independently of gameplay enablement.

WeatherGuard retains the shared DebugLoggingPolicy through its controller and diagnostics, rather than copying an initial boolean. Each diagnostic opportunity reads the current loaded global-or-feature setting and suppression/activity gates. This does not add automatic configuration-file reload; file edits still require the documented restart.

Detected loader Debug filtering is explained by a Warning followed immediately by Info instructions for the affected outputs; see [server configuration](../server-setup.md#configuration).

The native environment update depends on a main camera and can return before selecting a local environment on a headless server. EnvMan.IsWet and the reported server_environment are server-wide observations, not weather measurements at each remote player or structure. Fresh roof geometry and native cached cover remain separate observations; a dry server environment does not establish dry conditions at a player's swamp building. Ownership and geometry establish diagnostic scope; normal prevention requires removal of an actual native weather contribution.


### Exposure counts and biome evidence

WeatherGuardExposureTracking retains roof observations across the eligible player-built scope rather than the three-piece health sample. With either debug level enabled, at most twelve loaded pieces with ready physics context receive native RoofCheck and underwater probes per one-second scan. Probes never assign native fields and cannot produce prevention notices. Every scan refreshes the retained observations’ management state in both directions from current confirmed instance ownership, so reacquisition clears stale waiting status without refreshing roof-check timestamps. Scope removal drops the observation. SUMMARY labels all roof-derived counts as samples: `roof_sample_size_now`, `roof_sample_covered_now`, `roof_sample_exposed_now`, `roof_sample_exposed_managed_now`, `roof_sample_exposed_waiting_now`, `roof_sample_underwater_now` and `roof_sample_cache_disagreements_now`. `roof_unchecked_or_stale_now` describes the eligible pieces outside the current sample; sample size plus unchecked/stale equals the full `eligible_now` scope. Biome and oldest geometry-check age use `roof_sample_biomes` and `roof_sample_oldest_check_age_s`. Observations older than thirty seconds are unknown; the oldest current observation age describes the rolling census, and fixed probe limits appear in the startup settings detail. Large scopes can contain unknown entries.

Piece biomes come from WorldGenerator at each checked piece position; ready player biomes come from the shared presence roster's replicated character positions. The headless server environment's wet flag is labeled separately and must not describe player-local rain or a player's Wet status. Swamp geography gives local context but does not synthesize a native wetness value. A fresh roof/cache disagreement is labeled explicitly rather than describing an exposed piece as covered. Pieces can be acquired before native wetness qualifies; prevention is observed when the native weather contribution becomes due. Routine rejected owner transfers are counted in the interval network summary rather than printed repeatedly for each piece. The three-piece health sample remains bounded and is never an exposed-piece total.
