# AfkRaidProtection

Prevents a new automatic random raid from selecting a local area occupied only by players inferred idle. Enabled defaults true. An active or uncertain nearby player keeps raids working normally; an active player elsewhere does not remove protection from the AFK-only area. Existing raids, forced/scripted events and explicit admin starts continue normally.

Shared idle detection uses a five-minute default with configurable thresholds and grace. It cannot observe every client-local menu/inventory action. This feature does not make AFK players invulnerable or prevent ordinary enemies.

See [configuration](../server-setup.md#player-activity-and-sleep), [activity semantics](../architecture.md#shared-player-activity) and [specification](../specs/afk-raid-protection.md). Clients require no installation. Debug suppression leaves required gameplay tracking active; configuration changes require restart.

Diagnostic fields, rate limits and observation boundaries are described in the [diagnostic evidence contract](../specs/afk-raid-protection.md#diagnostic-evidence). Named hook registration is readiness; invocation counters show execution, and native server-state outcomes are separate from client delivery.
