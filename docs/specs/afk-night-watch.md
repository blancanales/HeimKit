# AFKNightWatch specification

## Purpose and server-only scope

AFKNightWatch owns configurable majority sleep and inferred-AFK exclusion from sleep on Windows dedicated servers. AFKRaidWatch independently owns raid protection. Clients require no mod. Supported versions appear only in [server setup](../server-setup.md#supported-environment). Native bed ownership, warmth, exposure, enemies, wetness, afternoon/night allowance, cooldown and sleep broadcasts remain unchanged. Other HeimKit features remain independent.

## Configuration

AFKNightWatchSettings binds `[AFKNightWatch]`: Enabled true, MajoritySleepEnabled true, ExcludeAFKFromSleep true, RequiredSleepingPercent 50 (range 1–100), RequireMoreThanPercent true and DebugMode false. Both sleep behaviors are independently selectable under Enabled. With both off no sleep hook is installed. Shared detection settings are bound by Configuration/PlayerActivitySettings under `[PlayerActivity]`: IdleTimeoutSeconds 300 (30–3600), JoinGraceSeconds 60 (0–600), MovementThresholdMeters 0.5 (0.1–5), LookThresholdDegrees 10 (1–90), SampleIntervalSeconds 1 (0.2–5). Changes require restart. The fresh layout has no migration, legacy alias or automatic legacy cleanup. Ordinary loader behavior preserves recognized values and adds missing settings; unbound values are not interpreted.

Global/feature debugging and the manual-only suppression override follow [server setup](../server-setup.md#hidden-per-feature-debug-suppression). Debugging cannot enable or disable gameplay inference.

## Sleep decisions

The dedicated server's EverybodyIsTryingToSleep vote uses strictly more than half by default: required sleepers are floor(participants × percentage / 100) + 1 below 100. Inclusive mode uses the ceiling with at least one sleeper. At 100, require all participants. Default counts for one, two, three and four participants are one, two, two and three sleepers. Zero participants or no known living bed sleeper cannot authorize sleep. Bed occupants are never excluded as idle. Known dead actors remain participants while their record exists, but cannot count as sleepers or idle.

Disabling majority while retaining exclusion requires every non-idle participant to sleep. Disabling exclusion retains idle participants. Unknown/loading actors preserve native voting only with a known living bed sleeper; otherwise the vote is false. Custom quorum evaluation runs only when a native vote is requested in the native permitted afternoon/night window and a known living sleeper exists. The feature never directly skips time or sends custom sleep messages. Native UpdateSleeping, bed checks, cooldown, time gates and SleepStart/SleepStop continue.

## Shared observations and lifecycle

Plugin owns one [PlayerServices hub](../architecture.md#player-activity). AFKNightWatch leases its activity service only when Enabled and ExcludeAFKFromSleep require inference. Majority sleep alone reads the shared presence character roster without idle tracking. The same observations can serve AFKRaidWatch during daylight; no consumer depends on another feature being enabled. Empty servers skip sampling and custom sleep evaluation while native processing continues. Missing readiness, unreliable inference or shared failure retains native behavior; recovery restarts grace.

AFKNightWatch owns compatibility, startup and isolated module failure. Its controller owns one activity lease and one sleep-vote registration. SleepVoteCoordinator installs one native vote prefix and separate read-only diagnostic observers; SleepQuorumRules owns quorum calculation. Models owns SleepDecision, NativeSleepObservation and NightWatchStatus. Shutdown and initialization rollback release every registration safely and repeatedly. Failed optional observations do not remove gameplay hooks.

## Diagnostic evidence

Info reports configured Enabled/Disabled and initialization Ready once per transition. Waiting, configuration and hook details use Debug. There is no plugin-wide Ready claim. Debug follows the shared [empty-server pause](../architecture.md#empty-server-debug-pause) and [repetition requirements](../architecture.md#repetition-and-volume-limits).

Sleep summaries contain last relevant participants, sleepers, threshold, excluded idle, native fallback, decision age and observed time-skip outcome/age. They emit at most once per minute on semantic change, with one compact unchanged heartbeat per five minutes. Hook invocation counts and advancing ages alone do not trigger full summaries. No recurring summary is emitted for empty or unobserved sleep-attempt scope. Targeted native sleep RESULT evidence has a separate six-per-minute cap, semantic gate/outcome deduplication and unknown values. One bounded DETAIL exposes sleep-hook counters alongside a changed summary, rather than mixing raid or routed counters into the summary. These streams together remain below the 60-record feature ceiling.

Read-only probes distinguish server sleeping/time-skipping before and after UpdateSleeping, afternoon/night, cooldown, game time, SkipToMorning calls and completed sessions. Registration proves initialization; a met quorum or skip invocation does not establish a completed time skip or client delivery. Significant outcomes retain ages. Shared actor transitions belong only to PlayerActivity diagnostics. Its bounded transition queue does not rotate unchanged actors. Warnings and errors remain independent of debug; diagnostic warning notices are throttled to five minutes, but the complete project incident-identity/repeat-count contract is not yet implemented for every diagnostic observer.

## Native mechanism and limitations

The mechanism derives from the installed supported Game.UpdateSleeping/EverybodyIsTryingToSleep, Bed.Interact, ZNet character ownership and replicated player/routed fields. Proprietary game code is not distributed. Other sleep patches can conflict. Server inference cannot observe every client-local action or prove human absence; initialization and compilation do not establish live Unity behavior.

## Presence lifecycle

The enabled module subscribes to Plugin's shared presence events. Empty or unavailable population releases its controller, native hooks and activity lease; the registration remains for reconnection. Occupancy permits controller creation only when the dedicated world is ready. Additional connections do not restart it. Shutdown/failure removes its registration, and a failed module is not automatically restarted by later occupancy. Native gameplay continues while this module is paused. Reconnection recreates resources with fresh activity grace. The registration and cleanup contract is defined in [architecture](../architecture.md#presence-driven-lifecycle).

## Presence lifecycle

The enabled module subscribes to Plugin's shared presence events. Empty or unavailable population releases its controller, native hooks and activity lease; the registration remains for reconnection. Occupancy permits controller creation only when the dedicated world is ready. Additional connections do not restart it. Shutdown/failure removes its registration, and a failed module is not automatically restarted by later occupancy. Native gameplay continues while this module is paused. Reconnection recreates resources with fresh activity grace. The registration and cleanup contract is defined in [architecture](../architecture.md#presence-driven-lifecycle).


## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into AFKNightWatch. The enabled, compatible module owns one presence registration and a controller only while occupied and native player/world services are ready. False/null releases the controller, sleep registration and any activity lease; true resumes with fresh inference grace once ready, with Tick retrying delayed readiness. Additional logins do not recreate resources. Idle inference is leased only when ExcludeAFKFromSleep requires it. Majority sleep alone uses shared presence. No empty-server exception applies. Shutdown/rollback/failure disposes registration and controller independently; failure does not disable other consumers.
