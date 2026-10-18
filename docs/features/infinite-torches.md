# InfiniteTorches

Automatically refills eligible torches and braziers using native fuel requests.

![InfiniteTorches: detect eligible lights below 75 percent fuel, refill automatically without inventory fuel, and retain native water, smoke and obstruction behavior; campfires and hearths excluded by default](../images/infinite-torches-workflow.jpg)

See [server setup](../server-setup.md) for installation/configuration and the [specification](../specs/infinite-torches.md) for eligibility, processing and limits.

## Behavior and diagnostics

Automatic Debug output follows the shared [five-minute empty-server pause](../architecture.md#empty-server-debug-pause). Reconnection resumes eligible diagnostics without restarting the feature; warnings, errors, requested reports and gameplay remain available.

`[Torches] DebugMode` defaults to `false` and is added automatically to the active `heimkit.cfg` on startup when missing, even if this feature is disabled. Existing values are preserved. See the [debug configuration rules](../server-setup.md#configuration) for global and hidden override precedence.

Eligible low-fuel torches and braziers receive the vanilla fuel-setting message, targeting their normal maximum fuel without spending player inventory materials. Requests begin below 75% capacity and are limited by MaxMessagesPerPass. Pending object tracking suppresses duplicate requests and allows retries after unresolved timeouts.

Normal Info logging groups observed refill updates into one entry per pass, such as `InfiniteTorches: Fuel increase observed for 32 light sources.` Debugging is not required. No entry is emitted when no sufficient fuel increase is observed; sending a request alone is not reported as a completed update. The count reflects observed fuel changes after pending requests, including possible simultaneous manual refilling.

The default allowlist covers wooden, iron, green, blue and wall torches, plus the hanging brazier and both standing brazier variants. Braziers refill without consuming coal; their normal wet-flame behavior remains. Campfires and hearths are excluded by default. Unsupported prefabs are warned about and skipped. Unloaded objects and ownership changes can delay refilling. Normal water, smoke and obstruction behavior remains; a fueled torch is not guaranteed to stay lit in every environment. Simultaneous manual refilling can still consume the normal fuel material.

Existing prefab lists are preserved on upgrade. To include braziers in an existing configuration, stop the server, append `piece_brazierceiling01,piece_brazierfloor01,piece_brazierfloor02` to `[Torches] Prefabs`, and restart. New configurations include them automatically.

Global or `[Torches] DebugMode` enables this feature’s diagnostics unless hidden suppression is set. Optional output uses Debug severity and requires the [BepInEx log filters](../server-setup.md#configuration) to include Debug. Debug output includes examined candidates, sent requests, skip reasons, pending records and deferred candidates. Changed-state summaries appear at most once per minute and follow the [shared repetition limits](../architecture.md#repetition-and-volume-limits); unchanged status uses compact heartbeats and empty scopes stay quiet; detailed refill requests are capped at four per minute. A sent request or observed fuel increase is not a delivery acknowledgement.

The [InfiniteTorches specification](../specs/infinite-torches.md) describes the implemented selection, networking, duplicate suppression and limits.

Per-feature diagnostic output and collection can be suppressed through the [hidden debug override](../server-setup.md#hidden-per-feature-debug-suppression), without disabling protection/refilling or standard startup, readiness, warnings and errors.

Player-dependent processing pauses when the server becomes empty or player presence is unavailable, and resumes on the first joined player once required world services are ready. Additional connections do not restart processing. The feature stays configured Enabled during this pause; native game systems continue. See the [presence lifecycle](../architecture.md#presence-driven-lifecycle).

Player-dependent processing pauses when the server becomes empty or player presence is unavailable, and resumes on the first joined player once required world services are ready. Additional connections do not restart processing. The feature stays configured Enabled during this pause; native game systems continue. See the [presence lifecycle](../architecture.md#presence-driven-lifecycle).

Gameplay lifecycle uses the shared joined-world state, established by native character spawning rather than an incoming connection or login handshake. After its own cleanup completes, the module acknowledges an empty-server pause to the shared service. One shared PlayerPresence notice confirms all features are inactive until a player joins; individual empty-server notices are suppressed. After a player joins, the shared active notice waits for successful initialization of all enabled consumers. Each enabled module reports Ready once when its required dedicated-server services become available, regardless of players. This indicates readiness to process, not installed player-dependent hooks or a verified gameplay effect. Individual processing activation and pause reasons belong in Debug, including later starts; no per-feature Resumed notice appears in normal output. Death/respawn does not end a joined session. See the [shared lifecycle](../architecture.md#presence-driven-lifecycle).

Unsupported configured prefab entries produce one warning with counts grouped by reason and a prompt to review Torches.Prefabs, rather than one warning for every entry.

A startup Debug policy entry identifies whether global or feature DebugMode enables diagnostics, or the hidden override suppresses them. It requires the [loader Debug filters](../server-setup.md#configuration) even when explaining suppression.

Detected loader Debug filtering is explained by a Warning followed immediately by Info instructions for the affected outputs; see [server configuration](../server-setup.md#configuration).

The shared [hidden version override](../server-setup.md#hidden-version-override) lets an operator authorize one exact game version after reviewing compatibility. It does not validate gameplay or bypass this feature's runtime checks.
