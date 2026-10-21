# TameTravel

Lets explicitly following or portal-waiting tamed **wolves, boars, lox, asksvin, hens and moose** accompany a player through linked outdoor wood or stone portals. The animal keeps its identity, name, health, tame state, saddle and other saved data. Riding, young/summoned creatures, dungeon transport and general distance catch-up are excluded.

## Commands

Stand within eight metres of the uniquely closest eligible tame. Received commands give a private vanilla HUD confirmation or a selection/error message.

Full-health animals remain eligible when Valheim omits their saved health value. A fresh stay command can stop your saved nearby followers after reconnect, without granting portal permission.

| Normal chat | Meaning | Reliable vanilla emote fallback |
| --- | --- | --- |
| `!follow` | Follow the player and authorize one portal trip | `/comehere` |
| `!stay` | Stop your nearby followers/waiting animals and cancel their portal travel | `/nonono` |
| `!wait` | Stop following but keep one nearby portal trip authorized | `/wave` |
| `!tame` | Show short command help | — |

Unknown slash commands are handled locally by unmodified clients, so `/follow`, `/stay` and `/wait` cannot be added by the server. Ordinary chat follows native delivery rules; if no HUD response arrives, use the emote fallback. A wave requests portal waiting only for an animal already associated with or currently following the sender. Separate closely packed animals before selecting one for follow or wait. `/nonono` instead addresses up to sixteen of your nearby following or waiting animals together, including saved followers after reconnect; repeat it if more remain. Another player's active association cannot be replaced.

## Following distance

`/comehere` starts following and requests server-controlled spacing. Wait for the private **Follow spacing active** message before relying on the adjusted distance. Defaults stop the animal four horizontal metres from you and resume following after five metres. Larger bodies automatically receive more clearance, and the final approach uses walking. Combat movement remains native.

The server controls at most eight followers globally by default. `/nonono` stops following and cancels portal permission; `/wave` stops following while retaining one nearby portal trip. These commands release follow-spacing control. A confirmed following portal arrival requests spacing again without granting another trip. Travel consent expiry does not end established spacing. After a restart, issue `/comehere` again.

## Portal travel

For a lox or a portal reached by stairs, first confirm follow, then request wait while still beside the animal. Leave it outside within twenty metres of the source portal. Walk to the portal yourself; the waiting animal need not pathfind up the stairs or enter the building. Source portals inside ordinary above-ground buildings are allowed; dungeon portals are excluded.

Enter a linked player-built portal while the animal remains within twenty metres of both you and the source. Stay within eight metres of the destination exit while transfers finish. Clear, dry native ground in front of the destination is required, especially for a lox. Placement does not use raised floors or stair landings. Wood and stone portals can be paired; pairs closer than twenty metres are excluded.

A private message identifies a detected portal attempt. Another message confirms arrival or distinguishes recorded relocation from unconfirmed destination ownership. If the attempt ends before an animal moves, the message explains the reason and asks for a fresh follow command beside the animal, followed by wait when appropriate. Do not assume a failed attempt leaves the previous trip authorization available. Fresh inside-trigger observations are accepted even when the player is already within the trigger area; the animal must still satisfy the same source-range and ownership checks.

A waiting animal remains stopped near its destination after arrival. Native stay/patrol can include local wandering; wait does not freeze physics or make an animal invulnerable. Following animals retain following behavior. Use stay to cancel waiting, or follow to resume walking with you.

Consent lasts five minutes and authorizes one traversal. Excessive movement from a waiting position, changed following, expiry or a replaced player session cancels consent. Issue a fresh command before another trip, after reconnecting or after a server restart. Player names must be unique. Native player portal restrictions remain in effect.

## Configuration

With DebugMode enabled, animal-selection evidence identifies nearby supported animals, their distances, individual rejection reasons, saved following and whether health is explicitly saved. Selection scope and candidate samples share eight records per minute, separately from command receipt/rejection evidence.

Traversal evidence separately reports missing/stale owner positions, trigger/pair checks, animal range and handoff gates, lost waiting consent, matched arrival/readiness and placement waits. Distance cancellations include the observed player position, expected exit, measured distance and entry/arrival ages; timeout has its own reason. Unchanged gate reasons combine for up to five minutes. Sixteen traversal records per minute are reserved separately from two movement/scope samples so following does not consume all travel diagnostics.

Use `[TameTravel]` in `heimkit.cfg`. Defaults enabled. Existing settings are preserved and missing defaults added. Restart applies configuration changes.

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | true | Enable TameTravel independently. |
| DebugMode | false | Optional feature diagnostics through the shared debug policy. |
| CommandRangeMeters | 8 | Nearby follow/stay selection distance. |
| FollowSpacingEnabled | true | Apply spacing using bounded server-owned following. |
| FollowStopDistanceMeters | 4 | Horizontal stopping distance, enlarged for body clearance. |
| FollowResumeDistanceMeters | 5 | Resume distance, at least effective stop plus one metre. |
| MaxControlledFollowers | 8 | Global server-controlled follower limit. |
| FollowerRangeMeters | 20 | Maximum source distance from player and portal. |
| FollowConsentSeconds | 300 | Fresh consent lifetime for one traversal. |
| TravelWindowSeconds | 30 | Time allowed to observe linked arrival. |
| MaxFollowersPerTraversal | 8 | Maximum followers reserved per trip. |

Normal output distinguishes recorded relocation from confirmed destination ownership. Normal output also reports named follow-spacing activation/release and failures. Debug separately records fresh emote application, recognized command receipt and concrete actor/selection/rate-limit rejection reasons, as well as bounded follow stop/resume, trigger, reservation and transaction evidence. Follow movement timing measures the changed follow path separately from controller timing; other native AI and physics remain outside those measurements. Reservation endings include an exact stop reason, phase age, barrier result and a snapshot of animal state before ownership release. See [configuration ranges and behavior](../specs/tame-travel.md), [shared limitations](../limitations.md#tametravel) and [server setup](../server-setup.md).
