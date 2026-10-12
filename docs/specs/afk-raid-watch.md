# AFKRaidWatch specification

## Behavior and dedicated-server feasibility

AFKRaidWatch independently protects inferred-idle-only locations from new normal automatic random raids on the Windows dedicated server. Supported versions are defined in [server setup](../server-setup.md#supported-environment). It uses the existing native RandEventSystem.UpdateRandomEvent scope and GetValidEventPoints output; no client installation or custom messages are required. Native horizontal event range and dungeon-height exclusion determine locality. A nearby known living actor must exist and every nearby actor must be alive, outside beds and inferred idle. Active, dead, loading or unreliable actors retain native eligibility. Active players elsewhere do not remove local protection.

Only normal random events inside the native automatic update are filtered. Existing raids, explicit/admin events, forced/named/scripted events and standalone selection retain native behavior. Protection never cancels or resets an event. Native timing, chance, biome, base and global-key gates remain unchanged. Candidates are evaluated when supplied by the game; there is no continuous selection scan. With no connected players, HeimKit filtering is bypassed while native processing continues.

## Configuration and ownership

[AFKRaidWatch] Enabled defaults true and DebugMode defaults false. Enabled alone controls protection. Shared inference thresholds belong to [PlayerActivity]. Settings are restart-required; no migration, legacy aliases or converters are provided. Global/feature debug and the manual-only suppression override follow [server setup](../server-setup.md#hidden-per-feature-debug-suppression).

The feature owns compatibility, controller, raid rules, scoped gameplay hooks, read-only outcome observers and diagnostics. It acquires one shared activity lease when initialized, releases it on shutdown or failure, and works independently of AFKNightWatch. Shared inference continues during daylight. Unavailable shared observations retain native behavior; recovery resets idle history and grace. See [shared services](../architecture.md#player-activity) for the lifecycle and integration contract.

## Diagnostics and limitations

Summaries describe actual candidate evaluations and exclusions, retaining outcome ages and unknown causes. Targeted bounded details retain native range/population, scope, gate counters, setter outcomes and start-request returns. Hook registration proves initialization only; server event state does not establish client delivery, enemy spawning or causality. Logging follows the [shared repetition requirements](../architecture.md#repetition-and-volume-limits) and empty-server debug pause. Changed summaries emit at most once per minute; unchanged relevant scope has one compact heartbeat per five minutes. Candidate RESULT and start/setter RESULT streams each cap at six per minute, and one targeted hook/schedule DETAIL can accompany a changed summary. The combined streams remain below the 60-record feature ceiling. Candidate reasons are semantically deduplicated in a 256-entry cache. Feature warning notices are throttled to five minutes, but full per-identity warning/error repetition counts and recovery notices remain incomplete for diagnostic observers. Diagnostics cannot change gameplay eligibility. Other event patches can conflict; inferred idle cannot prove human absence.

## Presence lifecycle

The enabled module subscribes to Plugin's shared presence events. Empty or unavailable population releases its controller, native hooks and activity lease; the registration remains for reconnection. Occupancy permits controller creation only when the dedicated world is ready. Additional connections do not restart it. Shutdown/failure removes its registration, and a failed module is not automatically restarted by later occupancy. Native gameplay continues while this module is paused. Reconnection recreates resources with fresh activity grace. The registration and cleanup contract is defined in [architecture](../architecture.md#presence-driven-lifecycle).

## Presence lifecycle

The enabled module subscribes to Plugin's shared presence events. Empty or unavailable population releases its controller, native hooks and activity lease; the registration remains for reconnection. Occupancy permits controller creation only when the dedicated world is ready. Additional connections do not restart it. Shutdown/failure removes its registration, and a failed module is not automatically restarted by later occupancy. Native gameplay continues while this module is paused. Reconnection recreates resources with fresh activity grace. The registration and cleanup contract is defined in [architecture](../architecture.md#presence-driven-lifecycle).


## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into AFKRaidWatch. The enabled, compatible module owns one presence registration and a controller only while occupied and native player/world services are ready. False/null releases its automatic candidate hooks and activity lease; true resumes with fresh inference grace once ready, with Tick retrying delayed readiness. Additional logins do not recreate resources. Each running controller acquires one shared activity lease. No empty-server exception applies. Shutdown/rollback/failure disposes registration and controller independently; failure does not disable other consumers.
