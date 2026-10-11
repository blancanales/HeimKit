# MajoritySleep specification

## Behavior and server-only mechanism

MajoritySleep changes only the dedicated server's native sleep vote. The default requires strictly more than 50 percent of participating players, with at least one actual living player in a valid native bed. One participating player can sleep alone. Zero participating players cannot skip time. Native bed ownership, exposure, warmth, enemies, wetness and station/time restrictions are unchanged.

The inspected supported game assembly contains Game.UpdateSleeping, which checks server ownership, afternoon/night, current time skipping and its cooldown before calling EverybodyIsTryingToSleep and broadcasting native SleepStart/SleepStop. The feature patches only EverybodyIsTryingToSleep. It never directly skips time or sends custom sleep messages. Existing vanilla clients understand the unchanged native sleep flow. Supported versions are in [server setup](../server-setup.md#supported-environment).

## Independent controls and quorum

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| MajoritySleep | Enabled | true | Apply the configurable quorum; restart after editing. |
| MajoritySleep | RequiredSleepingPercent | 50 | Threshold from 1 to 100. |
| MajoritySleep | RequireMoreThanPercent | true | Strictly exceed the percentage; 100 always means all participating players. |
| AfkSleepExclusion | Enabled | true | Independently exclude inferred-idle players; separate module. |

For strict thresholds below 100, required sleepers equal floor(participating players × percentage / 100) + 1. Non-strict mode uses the ceiling, always at least one. At 100, require all. Default results for 1, 2, 3 and 4 participating players are 1, 2, 2 and 3 sleepers. Invalid values are clamped by the loader's range validation.

With MajoritySleep disabled and AfkSleepExclusion enabled, all non-idle participating players must be in bed. With exclusion disabled, idle players count normally. With both disabled, the sleep hook is removed and vanilla voting applies. Loading/unknown actor data returns to the native vote rather than guessing, provided a living bed occupant is known; otherwise no modified vote authorizes sleeping. Known dead characters are not marked AFK or counted as actual sleepers; they remain non-idle participants while their character record is present. A bed occupant is never excluded as idle.

## Activity and lifecycle

The [shared activity contract](../architecture.md#shared-player-activity) describes the inferred-idle signals, grace, observability limits, clocks and independent consumer lifetime. Config definitions remain in each Settings class; processing and counters belong in each Controller; recurring summaries and samples belong in each Diagnostics class. A shared SleepVoteCoordinator owns the one native sleep hook so one module can be disabled without removing another's behavior. Shared services use no feature-specific dependency.

All modules honor the [hidden suppression-only debug policy](../server-setup.md#hidden-per-feature-debug-suppression). Debug summaries distinguish a satisfied quorum from an actual time skip; native cooldown/time checks can still prevent skipping. Startup/readiness/warnings/errors are ordinary output. Optional summaries are bounded to one per minute per module; independent sample-stream limits are defined below. No client installation, inventory changes or world-save fields are introduced.

## Evidence and limits

Primary evidence is the installed supported assembly inspected with [ILSpy](https://github.com/icsharpcode/ILSpy), specifically Game.UpdateSleeping/EverybodyIsTryingToSleep and Bed.Interact. Proprietary source is not distributed. [LetMeSleep](https://github.com/blockchaaain/LetMeSleep) is a public mechanism reference licensed GPL-3.0; no code is copied. HeimKit's implementation is independently derived from the installed interfaces. Server-observed bed/activity flags are network evidence, not a comprehensive anti-cheat validation of client actions. Other sleep patches can conflict with the vote hook; compatibility is not inferred from successful compilation.

## Diagnostic evidence

Ordinary registration records name Game.EverybodyIsTryingToSleep, Game.UpdateSleeping and EnvMan.SkipToMorning; registered means the patch installed, not that it ran. Summary hook counters separate update invocations, vote entries, empty rosters, no living sleeper, unknown-actor native fallbacks, returned true/false votes, native exceptions and SkipToMorning invocations. These cheap bounded counters remain independent of optional collection. Vote counts/requirements describe the last vote and include its age.

With effective diagnostics enabled, read-only UpdateSleeping probes sample server state, existing sleeping/time-skipping state, afternoon/night and native cooldown remaining. RESULT records report gates and before/after time-skipping/sleeping state, with sampled native game-clock values. A new active skip and a native sleep-session end are observed server state changes. A SkipToMorning invocation alone is a request/call, not that outcome. No state change and native exceptions remain distinct. Significant outcome counters survive sample omission; summary retains the last significant outcome and its age, alongside the latest gate and probe age. Old observations are not relabeled current or repeated as new outcomes.

The observer cannot establish which mod caused a state change or whether vanilla clients received/applied SleepStart/SleepStop; those values remain unknown/unobservable. Probes do not invoke, repeat or alter native gates or time controls. Diagnostic observation failures warn at most once per minute and leave native behavior unchanged. Global or per-feature suppression skips optional probe collection and clears retained optional evidence; cheap invocation counters continue.

Each module retains one SUMMARY per minute and a separate allowance of six changed native RESULT records per minute. Unchanged native samples are counted separately; omitted changed samples are summarized. World/controller shutdown releases hooks and evidence. AFK actor samples have their own six-record allowance as described in [AfkSleepExclusion](afk-sleep-exclusion.md).

Diagnostic observers have separate Harmony identities from gameplay controls. A failed observer registration reports its named hook with registered=false and retains gameplay hooks. Readiness lists only successful registrations. Initialized counters expose measured zero calls/results; an unavailable observer is identified by its registration warning rather than treated as working.
