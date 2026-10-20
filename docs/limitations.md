# Safety and limitations

This page collects HeimKit's gameplay, client and diagnostic limitations. Feature guides describe controls and supported behavior; specifications define implementation contracts.

## World safety

Use HeimKit at your own risk. Mods can corrupt worlds or cause data loss; we are not responsible for resulting damage. Back up the world before installing, updating or enabling HeimKit. Stop the server before replacing its DLL, editing configuration or copying save files. Follow [world backups](world-backups.md), including SharedMap's separate data directory.

## Server observations and logs

HeimKit works from server-observed state. Client ownership, unloaded objects and replication delays affect when changes are visible. Logs distinguish requests, accepted state and observed results; they do not confirm a player's screen or delivery to every client. Ready means native services are available. Compilation, compatibility checks and hook registration do not establish gameplay correctness.

Some diagnostic observers have incomplete incident repeat-count/recovery accounting. Debug output is sampled and capped. Missing detail lines do not establish missing processing. A replication sender may be forwarding another player's action, so sender identity does not prove who clicked a control. Other mods that patch the same systems can conflict. The manual game-version override changes the accepted version, not the API audit. See [supported environment](server-setup.md#supported-environment).

## SharedMap

### Discovery

Exploration uses periodic, replicated player positions. Sampling can miss travel between observations; teleports reveal endpoint circles only. Dead, unavailable and interior-height positions do not contribute. Camera visibility is not evaluated.

Resource, dungeon, runestone and spawner discovery waits for native records. An installed prefab does not mean the location or deposit exists in a particular world. Unplaced dungeon centers do not establish exterior entrances. Retrospective discovery uses accumulated community exploration, including imported table terrain; enabling it can reveal previously visited locations without a revisit.

Silver mining discovery depends on authenticated impacts or correlated digging observations. Missing baselines, delayed data or simultaneous nearby tools can leave a vein hidden until another qualifying impact. Missing/unloaded resource records do not prove depletion; unobserved destruction can leave historical markers. Copper/Silver percentages measure remaining mining sections, not ore yield, exposed volume or burial.

A dungeon percentage measures only its listed permanent pickup sites, not creature kills, chest contents or overall dungeon completion. Troll Cave markers do not report whether their Troll was defeated. Picking a site releases its item; it does not prove the item reached an inventory. Interior visits use sampled, living player positions and a unique saved location in the native interior zone; ambiguous matches do not unlock percentages. Matched already-picked items can also establish a prior visit; they do not identify who entered or when. Empty pickup records require a settling period after entry because replication can arrive late. Missing layouts, missing previously established records and unsupported objectives remain unknown. Respawning mushrooms/Jelly, random chest contents and destroyed pickups are excluded. Percentages already copied to personal maps by an earlier build cannot be withdrawn by the server.

Runestones use validated placed-proxy hierarchy offsets. Native StaticPhysics may settle their height locally; their horizontal landmark point remains fixed. Their static lore objects have no individual destruction record. Spawner markers require visible, player-destructible physical objects with actual persistent source records; an installed or historically registered prefab does not prove world presence. Only accepted source-ZDO destruction is retirement evidence. Destruction missed while processing is paused, before compatible metadata is available, or while the plugin is absent can leave a historical marker. Killing spawned creatures and scene unloading never retire a source. Spawner vertical settling retains the accepted source height; simultaneously stacked sources sharing one horizontal identity are omitted as ambiguous. Exact source reassociation rejects ambiguity; moved/replaced sources without a unique accepted identity retain the last state.

Interior runestones/spawners are omitted: publication requires verified saved-layout membership and an established exterior gateway association, which this marker path does not provide. High-altitude coordinates are never projected onto the outdoor map. Outdoor placement of an otherwise eligible physical source can qualify; this does not imply complete interior support. Unsupported animated/conditional hierarchies and excessive replicated scales are omitted.

### Client map updates

Players receive updates through Read Map after table reconciliation and replication. An early read may use older data. The server cannot force an open map to refresh or edit a personal client map.

Vanilla matches an incoming marker to any saved pin within one horizontal metre, without updating its label or icon. Personal pins can block insertion; distinct entrances closer than that can appear as one client pin. SharedMap additionally suppresses dungeon publications within ten horizontal metres of a stable actual-point anchor. Recognized dungeon-labeled hammer copies in that area are cleaned from shared table data; genuine exceptionally close entrances can therefore share one publication while retaining separate histories. Local stale-pin deletion followed by Read Map may be necessary. Portal/resource rename offsets and dungeon percentage/shared-checkmark offsets encourage replacement, but missed updates, reused slots, personal pins and regrouping can retain stale labels or checkmarks. Checkmark changes require allowed Write Map uploads; a stale or unchanged upload is not an explicit reversal. Personal pins belonging to the reader remain under that player's control. Publication offsets add at most about 2.83 metres; portal/resource rename offsets can add their own displacement.

### Player uploads

Write Map reads the table before uploading. Deleting a pin locally may therefore re-import it; later uploads from another player can restore it. Use [selected-pin removal](features/shared-map.md#removing-an-unwanted-shared-pin) for persistent table exclusions.

Checked pins are client-local until Write Map. With a client-owned table, the server may see its owner forwarding another player's upload. Native owner/author/display-name fields, proximity and table ownership cannot authenticate who clicked X. Automatic admin checked-pin deletion is unavailable; use server-side selected-pin administration. Shared X marks remain governed by SharePinCheckmarks. Table removals reach other players through shared data; already copied personal pins and the uploader's adopted X remain client-owned and may need local removal. Unrecognized ownership edits are preserved as ordinary pins rather than authorizing a global deletion.

AllowPlayerMapWrites=false rejects incoming server map fields. Vanilla still displays Write Map, its effects and its success message. A client owner can complete a local write/read before server correction; rejection cannot undo that read.

### Saved data

Community terrain, discoveries and marker state are separate from the native world save. A crash can lose changes since the last completed save. Restoring an older world with newer community data can reveal later progress. Corrupt or incompatible stores disable SharedMap instead of silently resetting data. Back up and restore matching files together.

## AFK detection, sleep and raids

Idle is an inference, not proof of human absence. Camera-only movement and unsent menu, map or inventory actions can be invisible. Unknown or unreliable observations retain native eligibility and restart inference grace. Sleep still depends on native bed/time checks; a permitted vote does not prove that a skip completed. Raid exclusion counts describe candidate filtering, not a canceled raid or a raid that was certain to occur. Ordinary enemies and existing raids remain active.

## WeatherGuard and InfiniteTorches

WeatherGuard coverage depends on loaded server context and ownership. Roof samples and coverage reports describe their stated regions and ages, not the whole world. Server weather is not a measurement of each client's local rain or Wet status; prevention waits for native damage timing.

Torch refilling can wait for loaded records, ownership and replication. Late delivery can overlap timeout retries. Native water, smoke and obstruction checks still affect whether a fueled light burns. Simultaneous manual refilling can consume its normal material, and an observed fuel increase cannot uniquely identify which operation caused it.

Return to [HeimKit](../README.md) or [server setup](server-setup.md).
