# MajoritySleep

Allows native night skipping when the configured share of participating players is in bed. Defaults require strictly more than half: one of two is insufficient, two of three is enough. One participating player can sleep alone; no time skip occurs without an actual living bed sleeper.

`[MajoritySleep] Enabled = true`, `RequiredSleepingPercent = 50` and `RequireMoreThanPercent = true` are the defaults. At 100 percent all participants must sleep. Native bed and time conditions remain. AfkSleepExclusion is an independent toggle, so majority sleep can operate with idle players included or excluded.

See [configuration](../server-setup.md#player-activity-and-sleep), [specification](../specs/majority-sleep.md) and [supported environment](../server-setup.md#supported-environment). Changes require a stopped server and restart. Clients need no mod. Debug output is bounded and honors the hidden feature suppression override; a met quorum does not by itself prove a time skip.

Diagnostic fields, rate limits and observation boundaries are described in the [diagnostic evidence contract](../specs/majority-sleep.md#diagnostic-evidence). Named hook registration is readiness; invocation counters show execution, and native server-state outcomes are separate from client delivery.
