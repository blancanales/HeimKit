# AfkSleepExclusion

Excludes players inferred idle from sleep requirements. Enabled defaults true and is independent of MajoritySleep and raid protection. Without majority sleep, all non-idle participants must sleep; with it, the configured quorum applies. Actual bed occupants always count, and zero active participants never cause a skip by themselves.

The server uses observable activity, with a five-minute default idle timeout and configurable join grace and movement/look thresholds. Observed crafting-station/container use keeps players active. Loading/death/unknown state is conservative. Some stationary personal-inventory, menu and unsent-chat actions cannot be seen; idle classification is an inference and can differ from whether the human is present.

See [configuration](../server-setup.md#player-activity-and-sleep), [activity semantics](../architecture.md#shared-player-activity) and [specification](../specs/afk-sleep-exclusion.md). Clients need no mod; changes require server restart. Debug suppression affects output, not the activity collection required for this feature.

Diagnostic fields, rate limits and observation boundaries are described in the [diagnostic evidence contract](../specs/afk-sleep-exclusion.md#diagnostic-evidence). Named hook registration is readiness; invocation counters show execution, and native server-state outcomes are separate from client delivery.
