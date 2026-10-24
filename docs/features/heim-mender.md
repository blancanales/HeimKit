# HeimMender

Gradually repairs damaged player-built static structures and accepts bounded administrative repair requests. It restores existing pieces to native maximum health without consuming materials or changing their creator. Destroyed pieces and world-created ruins are excluded. See [specification](../specs/heim-mender.md) and [shared limitations](../limitations.md).

## Settings

Settings are in `[HeimMender]` in the loader-provided `heimkit.cfg`. This section reloads during play, including Enabled and DebugMode; other features keep their documented restart requirements. Save a complete edit and allow a few seconds for application. Invalid values retain the previously loaded section and produce a warning.

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | true | Enable the module |
| DebugMode | false | Optional shared-policy diagnostics |
| AutomaticEnabled | true | Gradual repair cycles |
| IntervalSeconds | 300 | Seconds between automatic cycles |
| RepairChancePercent | 50 | Chance per damaged candidate per cycle |
| MaxRepairsPerCycle | 10 | Maximum automatic repair requests per cycle |
| ScanRangeMeters | 64 | Range around a rotating ready player |
| DamageQuietSeconds | 60 | Deferral after observed health loss |
| HostileClearanceMeters | 30 | Automatic deferral near untamed MonsterAI; 0 disables |
| AdminRadiusMeters | 64 | Default manual repair radius |
| AdminMaxPieces | 200 | Maximum requests per manual batch |
| RepairRequestId | 0 | Increase to request manual repairs during play |
| RepairTarget | empty | Exact joined player name or peer UID; empty requires one ready player |

Five-minute cycles and a ten-piece cap provide gradual maintenance. Chance and caps apply independently: a cycle may repair fewer pieces. Automatic damage/threat deferrals do not classify the original cause of damage. Manual batches bypass those deferrals, cycle timing and chance.

## Administrative repair

Set RepairTarget to the intended joined player, then increase RepairRequestId and save the config. The service repairs eligible pieces within AdminRadiusMeters of that player's observed position. Startup establishes the saved request ID as its baseline; it never replays a request. One batch runs at a time. An empty server, ambiguous target or busy batch rejects the request.

From the **dedicated-server terminal**, use `heimmender repair <peerUID-or-single-word-name> [radius]` or `heimmender status`. Configuration supports full player names containing spaces. Vanilla clients do not recognize this new console command. A normal server-routed `!heimmender repair` chat message also accepts requests from native server-list admins, using their authenticated connection and own position. See [native delivery limitations](../limitations.md).

## Scope and diagnostics

Player-built static building-category walls, floors, roofs, structural beams, doors and gates qualify, including weather-immune materials. Furniture, crafting stations, containers, vehicles and moving pieces are excluded. Repairs use the existing native owner; unavailable owners postpone work.

Normal output reports accepted/rejected requests and confirmed repair totals per completed batch. Debug SUMMARY records show damaged candidates, quiet/threat deferrals, unavailable owners and selected requests. Sampled ACTION records include ZDO identity, health, maximum health and owner; RESULT records separate requests, confirmations and timeouts. A sent request alone is not a repair confirmation.

The observation budget retains damaged candidates rather than healthy building pieces. Completed repairs release their slots. Normal output warns if damaged-candidate capacity is exhausted and reports when capacity recovers.

Automatic cycles reuse player/threat position snapshots during selection and recheck live conditions before issuing repairs. Unrelated routed traffic is skipped before chat payload decoding.

Discovery reuses nearby candidate records and accepts native record changes. Entering another area seeds discovery again; action eligibility and timing retain the configured rules. See the [shared discovery design](../architecture.md#record-driven-candidate-discovery).
