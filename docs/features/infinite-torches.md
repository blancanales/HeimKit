# Infinite Torches

Automatically refills eligible torches using native fuel requests.

See [server setup](../server-setup.md) for installation/configuration and the [specification](../specs/infinite-torches.md) for eligibility, processing and limits.

## Behavior and diagnostics

Automatic Debug output follows the shared [five-minute empty-server pause](../architecture.md#empty-server-debug-pause). Reconnection resumes eligible diagnostics without restarting the feature; warnings, errors, requested reports and gameplay remain available.

`[Torches] DebugMode` defaults to `false` and is added automatically to the active `heimkit.cfg` on startup when missing, even if this feature is disabled. Existing values are preserved. See the [debug configuration rules](../server-setup.md#configuration) for global and hidden override precedence.

Eligible low-fuel torches receive the vanilla fuel-setting message, targeting their normal maximum fuel without spending player inventory materials. Requests begin below 75% capacity and are limited by MaxMessagesPerPass. Pending object tracking suppresses duplicate requests and allows retries after unresolved timeouts.

The default allowlist covers wooden, iron, green, blue and wall torches. Unsupported prefabs are warned about and skipped. Unloaded objects and ownership changes can delay refilling. Normal water, smoke and obstruction behavior remains; a fueled torch is not guaranteed to stay lit in every environment. Simultaneous manual refilling can still consume resin.

Global or `[Torches] DebugMode` enables this feature’s diagnostics unless hidden suppression is set. Optional output uses Debug severity and requires the [BepInEx log filters](../server-setup.md#configuration) to include Debug. Debug output includes examined candidates, sent requests, skip reasons, pending records and deferred candidates. Changed-state summaries appear at most once per minute and follow the [shared repetition limits](../architecture.md#repetition-and-volume-limits); unchanged status uses compact heartbeats and empty scopes stay quiet; detailed refill requests are capped at four per minute. A sent request or observed fuel increase is not a delivery acknowledgement.

The [Infinite Torches specification](../specs/infinite-torches.md) describes the implemented selection, networking, duplicate suppression and limits.

Per-feature diagnostic output and collection can be suppressed through the [hidden debug override](../server-setup.md#hidden-per-feature-debug-suppression), without disabling protection/refilling or standard startup, readiness, warnings and errors.

Player-dependent processing pauses when the server becomes empty or player presence is unavailable, and resumes on the first connection once required world services are ready. Additional connections do not restart processing. The feature stays configured Enabled during this pause; native game systems continue. See the [presence lifecycle](../architecture.md#presence-driven-lifecycle).

Player-dependent processing pauses when the server becomes empty or player presence is unavailable, and resumes on the first connection once required world services are ready. Additional connections do not restart processing. The feature stays configured Enabled during this pause; native game systems continue. See the [presence lifecycle](../architecture.md#presence-driven-lifecycle).
