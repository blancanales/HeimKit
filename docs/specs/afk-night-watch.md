# AFKNightWatch specification

Gameplay and observation limitations: [safety and limitations](../limitations.md).
## Purpose and server-only scope

AFKNightWatch owns configurable majority sleep and inferred-AFK exclusion from sleep on Windows dedicated servers. AFKRaidWatch independently owns raid protection. Clients require no mod. Supported versions appear only in [server setup](../server-setup.md#supported-environment). Native bed ownership, warmth, exposure, enemies, wetness, afternoon/night allowance, cooldown and sleep broadcasts remain unchanged. Other HeimKit features remain independent.

## Configuration

AFKNightWatchSettings binds `[AFKNightWatch]`: Enabled true, MajoritySleepEnabled true, ExcludeAFKFromSleep true, RequiredSleepingPercent 50 (range 1–100), RequireMoreThanPercent true, SleepDelaySeconds 30 (range 0–300 real seconds) and DebugMode false. Both sleep behaviors are independently selectable under Enabled. With both off no sleep hook is installed. Shared detection settings are bound by Configuration/PlayerActivitySettings under `[PlayerActivity]`: IdleTimeoutSeconds 300 (30–3600), JoinGraceSeconds 60 (0–600), MovementThresholdMeters 0.5 (0.1–5), LookThresholdDegrees 10 (1–90), SampleIntervalSeconds 1 (0.2–5). Changes require restart. The fresh layout has no migration, legacy alias or automatic legacy cleanup. Ordinary loader behavior preserves recognized values and adds missing settings; unbound values are not interpreted.

Global/feature debugging and the manual-only suppression override follow [server setup](../server-setup.md#hidden-per-feature-debug-suppression). Debugging cannot enable or disable gameplay inference. Shared PlayerActivity observations also have their own DebugMode setting and manual suppression override; see [shared services](../architecture.md#integrating-a-consumer).

## Sleep decisions

The dedicated server's EverybodyIsTryingToSleep vote uses strictly more than half by default: required sleepers are floor(participants × percentage / 100) + 1 below 100. Inclusive mode uses the ceiling with at least one sleeper. At 100, require all participants. Default counts for one, two, three and four participants are one, two, two and three sleepers. Zero participants or no known living bed sleeper cannot authorize sleep. Bed occupants are never excluded as idle. Known dead actors remain participants while their record exists, but cannot count as sleepers or idle.

Disabling majority while retaining exclusion requires every non-idle participant to sleep. Disabling exclusion retains idle participants. Unknown/loading actors preserve native voting only with a known living bed sleeper; otherwise the vote is false. Custom quorum evaluation runs only when a native vote is requested in the native permitted afternoon/night window and a known living sleeper exists. When quorum is met while some roster players are not in bed, the vote remains false until SleepDelaySeconds of continuous eligibility have elapsed. Everyone in bed bypasses the wait immediately. A lost quorum, unavailable/unknown observations, missing living sleeper, native error, clock rollback, leaving the permitted sleep window, existing time skip, disposal or pause clears the timer; the next eligible attempt gets a fresh wait. AFK-excluded players outside bed still make the vote non-unanimous for this grace period. Zero delay restores immediate quorum authorization. A changed roster is evaluated against the current threshold; changes that retain quorum keep the original deadline. Delay uses monotonic real time, not accelerated game time. Native cooldown can postpone the actual start further. AFKNightWatchSleepDelay owns the per-registration timer. The feature never directly skips time or sends custom sleep messages. Native UpdateSleeping, bed checks, cooldown, time gates and SleepStart/SleepStop continue.

## Shared observations and lifecycle

Plugin owns one [PlayerServices hub](../architecture.md#player-activity). AFKNightWatch leases its activity service only when Enabled and ExcludeAFKFromSleep require inference. Majority sleep alone reads the shared presence character roster without idle tracking. The same observations can serve AFKRaidWatch during daylight; no consumer depends on another feature being enabled. Empty servers skip sampling and custom sleep evaluation while native processing continues. Missing readiness, unreliable inference or shared failure retains native behavior; recovery restarts grace.

Use the shared [activity signals and recovery rules](../architecture.md#player-activity).

AFKNightWatch owns compatibility, startup and isolated module failure. Its controller owns one activity lease and one sleep-vote registration. AFKNightWatchSleepVoteCoordinator installs one native vote prefix and separate read-only diagnostic observers; AFKNightWatchSleepQuorumRules owns quorum calculation. Models owns SleepDecision, NativeSleepObservation and NightWatchStatus. Shutdown and initialization rollback release every registration safely and repeatedly. Failed optional observations do not remove gameplay hooks.

## Diagnostic evidence

Info reports configured Enabled/Disabled and one Ready when dedicated-server services are available, regardless of players. PlayerPresence alone reports normal processing active/inactive status; individual processing transitions and pause reasons use Debug. Waiting, configuration and hook details use Debug. There is no plugin-wide Ready claim. Debug follows the shared [empty-server pause](../architecture.md#empty-server-debug-pause) and [repetition requirements](../architecture.md#repetition-and-volume-limits).

Sleep summaries contain last relevant participants, sleepers, threshold, excluded idle, native fallback, decision age and observed time-skip outcome/age. They emit at most once per minute on semantic change, with one compact unchanged heartbeat per five minutes. Hook invocation counts and advancing ages alone do not trigger full summaries. No recurring summary is emitted for empty or unobserved sleep-attempt scope. Targeted native sleep outcome sample evidence has a separate six-per-minute cap, semantic gate/outcome deduplication and unknown values. One bounded evidence record exposes sleep-hook counters alongside a changed summary, rather than mixing raid or routed counters into the summary. These streams together remain below the 60-record feature ceiling.

Read-only probes distinguish server sleeping/time-skipping before and after UpdateSleeping, afternoon/night, cooldown, game time, SkipToMorning calls and completed sessions. Significant outcomes retain ages. Shared actor observations and ordinary AFK transitions belong to the shared PlayerActivity service. Its bounded transition queue contains changed actors only. Warnings/errors remain independent of Debug; diagnostic warning notices are throttled to five minutes. Incident-accounting limits are covered in [limitations](../limitations.md#server-observations-and-logs).

Standard Info reports one confirmed sleep start under the configured rules while some roster players were not in bed. The accepted known vote must belong to the same native UpdateSleeping call that changes server sleeping/time-skipping from inactive to active. A met quorum, pending delay, cooldown-blocked vote, native error, missing observation, existing skip on initialization or unanimous sleep does not produce this notice. No generic start/end Info is emitted. AFKNightWatchSleepNotices owns transition deduplication and resets on controller disposal. Debug carries the effective majority/exclusion switches, percentage and strictness, configured delay, remaining delay, roster/participant/sleeper/excluded counts and required count; native start/end and timing gates remain optional evidence. Native outcome and notice callbacks preserve native exception propagation. See the [standard logging contract](../architecture.md#standard-feature-events-and-grouping).

## Native mechanism and limitations

The mechanism derives from the installed supported Game.UpdateSleeping/EverybodyIsTryingToSleep, Bed.Interact, ZNet character ownership and replicated player/routed fields. Proprietary game code is not distributed.

## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into AFKNightWatch. The enabled, compatible module owns one presence registration and a controller only while occupied and native player/world services are ready. False/null releases the controller, sleep registration and any activity lease; true resumes with fresh inference grace once ready, with Tick retrying delayed readiness. Additional logins do not recreate resources. Idle inference is leased only when ExcludeAFKFromSleep requires it. Majority sleep alone uses shared presence. No empty-server exception applies. Shutdown/rollback/failure disposes registration and controller independently; failure does not disable other consumers.

Shared PlayerActivity owns [AFK notices](../architecture.md#ordinary-afk-notices); consumers do not duplicate them.

## Game-version compatibility

Use the shared [compatibility contract](../architecture.md#configuration) before activation.

## HeimKit Core integration

The performance rebuild in `src/HeimKit/Features/` consumes PlayerServices presence/activity leases, Core native target resolution and shared diagnostic budgets; no world subscription. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.

Fallback sleep votes reuse actor/vote collection buffers. Shared activity sampling reuses temporary collections and caches exact equipment-field snapshots before formatting changed equipment strings. Quorum, uncertainty handling, grace and AFK transitions retain their existing rules.