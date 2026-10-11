# Infinite Torches

Automatically refills eligible torches using native fuel requests.

See [server setup](../server-setup.md) for installation/configuration and the [specification](../specs/infinite-torches.md) for eligibility, processing and limits.

## Behavior and diagnostics

Eligible low-fuel torches receive the vanilla fuel-setting message, targeting their normal maximum fuel without spending player inventory materials. Requests begin below 75% capacity and are limited by MaxMessagesPerPass. Pending object tracking suppresses duplicate requests and allows retries after unresolved timeouts.

The default allowlist covers wooden, iron, green, blue and wall torches. Unsupported prefabs are warned about and skipped. Unloaded objects and ownership changes can delay refilling. Normal water, smoke and obstruction behavior remains; a fueled torch is not guaranteed to stay lit in every environment. Simultaneous manual refilling can still consume resin.

Debug output includes examined candidates, sent requests, skip reasons, pending records and deferred candidates. Status summaries appear at most once per minute; detailed refill requests are capped at four per minute. A sent request or observed fuel increase is not a delivery acknowledgement.

The [Infinite Torches specification](../specs/infinite-torches.md) describes the implemented selection, networking, duplicate suppression and limits.

Per-feature diagnostic output and collection can be suppressed through the [hidden debug override](../server-setup.md#hidden-per-feature-debug-suppression), without disabling protection/refilling or standard startup, readiness, warnings and errors.
