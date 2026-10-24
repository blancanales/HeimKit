# TameTravel

Lets explicitly following or portal-waiting tamed **wolves, boars, lox, asksvin, hens and moose** accompany a player through linked outdoor wood or stone portals. The animal keeps its identity, name, health, tame state, saddle and other saved data. Riding, young/summoned creatures, dungeon transport and general distance catch-up are excluded.

## Commands

Stand within eight metres of the uniquely closest eligible tame. Received commands give a private vanilla HUD confirmation or a selection/error message.

Full-health animals remain eligible when Valheim omits their saved health value. A fresh stay command can stop your saved nearby followers after reconnect, without granting portal permission.

| Normal chat | Meaning | Reliable vanilla emote fallback |
| --- | --- | --- |
| `!follow` | Follow the player through successive portal trips until stopped | `/comehere` |
| `!stay` | Stop your nearby followers/waiting animals and cancel their portal travel | `/nonono` |
| `!wait` | Stop following but keep one nearby portal trip authorized | `/wave` |
| `!tame` | Show short command help | â€” |

Unknown slash commands are handled locally by unmodified clients, so `/follow`, `/stay` and `/wait` cannot be added by the server. Ordinary chat follows native delivery rules; if no HUD response arrives, use the emote fallback. A wave requests portal waiting only for an animal already associated with or currently following the sender. Separate closely packed animals before selecting one for follow or wait. `/nonono` instead addresses up to sixteen of your following or associated animals within thirty-two metres together, including saved followers after reconnect; repeat it if more remain. Another player's active association cannot be replaced.

## Following distance

`/comehere` starts following and requests server-controlled spacing. Wait for the private **Follow spacing active** message before relying on the adjusted distance. Smaller animals default to stopping four horizontal metres from you and resuming after five metres. A lox stops at least six metres away and resumes after eight metres, leaving room for its visible body. Larger configured distances and scaled collision bodies can increase these distances further. The final approach uses walking. Combat movement remains native.

The server controls at most eight followers globally by default. `/nonono` stops following and cancels portal permission; `/wave` stops following while retaining one nearby portal trip. These commands release follow-spacing control. Stop takes priority over pending following; stay confirmation waits for native command state. A nearby `/nonono` received during a following handoff cancels its portal permission immediately and queues a single native stop after the reservation releases; private feedback distinguishes pending from confirmed stay. A confirmed following portal arrival resumes following and requests spacing again. Follow permission remains active for your current player session until stopped or invalidated; it does not expire after five minutes. After a restart, issue `/comehere` again.

## Portal travel

Use `/comehere` once beside the animal, then enter the portal yourself. No `/wave` is needed. The follower must remain within twenty metres of you and the source portal when entry is detected; it need not enter the trigger, climb the stairs or fit inside the building. HeimKit begins its source ownership handoff at detected entry to hold it for travel, then resumes following after confirmed arrival. `/wave` is an optional alternative when you want one portal trip followed by staying at the destination. Source portals inside ordinary above-ground buildings are allowed; dungeon portals are excluded.

Enter a linked player-built portal while the animal remains within twenty metres of both you and the source. After arrival, you can walk clear of the portal and outside the building while remaining within forty-eight metres of the destination exit. HeimKit densely searches nearby forward-left, forward-right and centre positions before widening around the exit and your current position. It accepts dry terrain and low, solid structural paving, with clearance based on the animal's actual size. A player-sized access route must connect you to the landing area, allowing doorway exits and short detours around furniture while rejecting walls and distant detours. Animals remain in front of the portal with body clearance. Landing positions stay within forty-eight horizontal metres of the exit and within twenty-four vertical metres. Placement resumes across passes for up to thirty seconds, within the overall journey deadline. The full animal needs support, body room and a clear walkout without overhead building pieces. Wood and stone portals can be paired; pairs closer than twenty metres are excluded. Matching blank tags are supported when vanilla has linked the pair. A witnessed entry can wait up to six seconds for native link updates; destination arrival and safe-placement requirements still apply. See [placement limitations](../limitations.md#tametravel).

A private message identifies a detected portal attempt. Another message confirms arrival or distinguishes recorded relocation from unconfirmed destination ownership. If an attempt ends before an animal moves, the message reports the outcome. An unchanged follow association remains active for another portal attempt; consumed optional waits require a fresh command. After a completed or cancelled attempt, leave the relevant portal trigger before entering it again. Fresh inside-trigger observations are accepted even when the player is already within the trigger area; the animal must still satisfy the same source-range and ownership checks.

A waiting animal remains stopped near its destination after arrival. Native stay/patrol can include local wandering; wait does not freeze physics or make an animal invulnerable. Following animals retain following behavior. Use stay to cancel waiting, or follow to resume walking with you.

Follow permission spans successive portal trips in the current player session and ends on stay, wait, changed following, death, invalid identity, disconnect or character replacement. Reconnects and server restarts require a fresh `/comehere`; saved follow names do not recreate travel permission. Optional waiting consent lasts five minutes and authorizes one traversal; expiry or excessive movement from its waiting position cancels it. Player names must be unique. Native player portal restrictions remain in effect.

## Configuration

Idle follow/physics hooks skip component checks, unrelated network packets avoid payload copying, and recurring processing reuses working buffers. Existing following and consent rules remain.

With DebugMode enabled, animal-selection evidence identifies nearby supported animals, their distances, individual rejection reasons, saved following and whether health is explicitly saved. Selection scope and candidate samples share eight records per minute, separately from command receipt/rejection evidence.

Traversal evidence separately reports missing/stale owner positions, trigger/pair checks, animal range and handoff gates, lost waiting consent, matched arrival/readiness and placement waits. Distance cancellations include the observed player position, expected exit, measured distance and entry/arrival ages; timeout has its own reason. Unchanged gate reasons combine for up to five minutes. Routine source polling allows at most one sample per player per minute and cannot exhaust substantive traversal evidence. Pair failures identify the exact link, tag, destination eligibility or distance check, with supporting native IDs and tags. Six substantive traversal records, two routine source-poll records and eight independent transaction-ending records per minute are reserved separately from two movement/scope samples so following does not consume all travel diagnostics.

Use `[TameTravel]` in `heimkit.cfg`. Defaults enabled. Existing settings are preserved and missing defaults added. Restart applies configuration changes.

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | true | Enable TameTravel independently. |
| DebugMode | false | Optional feature diagnostics through the shared debug policy. |
| CommandRangeMeters | 8 | Follow/wait selection distance. Stay reaches your associated animals within 32 metres. |
| FollowSpacingEnabled | true | Apply spacing using bounded server-owned following. |
| FollowStopDistanceMeters | 4 | Horizontal stopping distance, enlarged for body clearance; lox use at least six metres. |
| FollowResumeDistanceMeters | 5 | Resume distance, at least effective stop plus one metre; lox use at least eight metres and a two-metre gap. |
| MaxControlledFollowers | 8 | Global server-controlled follower limit. |
| FollowerRangeMeters | 20 | Maximum source distance from player and portal. |
| FollowConsentSeconds | 300 | Optional single-trip waiting consent lifetime; active following has no timer. |
| TravelWindowSeconds | 30 | Time allowed to observe linked arrival. |
| MaxFollowersPerTraversal | 8 | Maximum followers reserved per trip. |

Normal output distinguishes recorded relocation from confirmed destination ownership. Normal output also reports named follow-spacing activation/release and failures. Debug separately records fresh emote application, recognized command receipt and concrete actor/selection/rate-limit rejection reasons, as well as bounded follow stop/resume, trigger, reservation and transaction evidence. Follow movement timing measures the changed follow path separately from controller timing; other native AI and physics remain outside those measurements. Reservation endings include an exact stop reason, phase age, barrier result and a snapshot of animal state before ownership release. See [configuration ranges and behavior](../specs/tame-travel.md), [shared limitations](../limitations.md#tametravel) and [server setup](../server-setup.md).

Portal selection reuses working buffers. Players without travel consent or an active journey retain lightweight command observation; active traversal keeps its existing sampling and readiness checks.

Destination snapshots preserve native per-client replication bookkeeping. Unconfirmed handoffs receive a limited number of refreshes, then release ownership to normal native assignment; they do not leave an indefinite reservation. Debug reservation endings report whether follow permission continues, refresh count, placement attempts and candidates, rejection counts, the last landing probe and accepted support height, synchronous context/search timings and actual moved position and forward/right offsets from the portal. Access rejections distinguish missing/blocked navigation, endpoint snapping, excessive detours and physical wall collisions. Placement waits retain their bounded, deduplicated evidence stream; final transaction evidence has a separate allowance.


## HeimKit log topics

- `command`: Command receipt, selection and rejection.
- `follow`: Native following and spacing observations.
- `travel`: Portal entry, transfer progress and completion.

Shared lifecycle, configuration, scope and timing topics follow the [logging contract](../architecture.md#logging). Verbose evidence requires effective debugging and the shared DebugVerbosity setting.
