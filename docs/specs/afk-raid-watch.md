# AFKRaidWatch specification

Gameplay and observation limitations: [safety and limitations](../limitations.md).
## Behavior and dedicated-server feasibility

AFKRaidWatch independently protects inferred-idle-only locations from new normal automatic random raids on the Windows dedicated server. Supported versions are defined in [server setup](../server-setup.md#supported-environment). It uses the existing native RandEventSystem.UpdateRandomEvent scope and GetValidEventPoints output; no client installation or custom messages are required. Native horizontal event range and dungeon-height exclusion determine locality. A nearby known living actor must exist and every nearby actor must be alive, outside beds and inferred idle. Active, dead, loading or unreliable actors retain native eligibility. Active players elsewhere do not remove local protection.

Only normal random events inside the native automatic update are filtered. Existing raids, explicit/admin events, forced/named/scripted events and standalone selection retain native behavior. Protection never cancels or resets an event. Native timing, chance, biome, base and global-key gates remain unchanged. Candidates are evaluated when supplied by the game; there is no continuous selection scan. With no connected players, HeimKit filtering is bypassed while native processing continues.

## Configuration and ownership

[AFKRaidWatch] Enabled defaults true and DebugMode defaults false. Enabled alone controls protection. Shared inference thresholds belong to [PlayerActivity]. Settings are restart-required; no migration, legacy aliases or converters are provided. Global/feature debug and the manual-only suppression override follow [server setup](../server-setup.md#hidden-per-feature-debug-suppression). Shared PlayerActivity observations also have their own DebugMode setting and manual suppression override; see [shared services](../architecture.md#integrating-a-consumer).

The feature owns compatibility, controller, raid rules, scoped gameplay hooks, read-only outcome observers and diagnostics. Each running controller holds one shared activity lease, released when the server empties, on shutdown or on failure; it works independently of AFKNightWatch. Shared inference continues during daylight. Unavailable shared observations retain native behavior; recovery resets idle history and grace. See [shared services](../architecture.md#player-activity) for the lifecycle and integration contract.

## Diagnostics and limitations

Summaries describe actual candidate evaluations and exclusions, retaining outcome ages and unknown causes. Targeted bounded details retain native range/population, scope, gate counters, setter outcomes and start-request returns. Logging follows the [shared repetition requirements](../architecture.md#repetition-and-volume-limits) and empty-server debug pause. Changed summaries emit at most once per minute; unchanged relevant scope has one compact heartbeat per five minutes. Candidate outcome sample and start/setter outcome sample streams each cap at six per minute, and one targeted hook/schedule evidence can accompany a changed summary. The combined streams remain below the 60-record feature ceiling. Candidate reasons are semantically deduplicated in a 256-entry cache. Feature warning notices are throttled to five minutes. Incident-accounting limits are covered in [limitations](../limitations.md#server-observations-and-logs). Diagnostics cannot change gameplay eligibility.

Standard Info output groups all actual candidate exclusions from one outer native automatic selection pass into a single count. AFKRaidWatchSelectionNotices accumulates the count while the gameplay filter operates, independently of optional diagnostics. Nested automatic scopes share the outer batch; explicit/admin/standalone scope never contributes. Empty batches are quiet, failed passes emit no successful summary, and disposal clears unfinished counts. The message reports the excluded candidate count. Individual candidate traces remain bounded Debug evidence; native exceptions are preserved. See the [standard logging contract](../architecture.md#standard-feature-events-and-grouping).

## Player-service integration

Follow the [required hookup](../architecture.md#required-feature-hookup). Plugin injects PlayerServices into AFKRaidWatch. The enabled, compatible module owns one presence registration and a controller only while occupied and native player/world services are ready. False/null releases its automatic candidate hooks and activity lease; true resumes with fresh inference grace once ready, with Tick retrying delayed readiness. Additional logins do not recreate resources. Each running controller acquires one shared activity lease. No empty-server exception applies. Shutdown/rollback/failure disposes registration and controller independently; failure does not disable other consumers.

Shared PlayerActivity owns [AFK notices](../architecture.md#ordinary-afk-notices); consumers do not duplicate them.

## Game-version compatibility

Use the shared [compatibility contract](../architecture.md#configuration) before activation.

## HeimKit Core integration

The performance rebuild in `src/HeimKit/Features/` consumes PlayerServices presence/activity leases, Core native target resolution and shared diagnostic budgets; no world subscription. Diagnostics use the shared `FeatureDiagnostics` gate and budget. The [Core ownership table](../architecture.md#core-layer-and-anti-duplication) and [rebuild specification](../ai/heimkit-rebuild-spec.md) govern placement; the behavior and configuration contract above applies unchanged.
