# DoorKeeper

Closes supported player-built doors and gates after five seconds observed open, once the doorway is clear. It never automatically opens doors. Keyed, world/dungeon, one-way, inverted and unsupported controls are excluded.

Supported types are wood door, wood gate, wood fence gate, darkwood gate, iron gate, stave gate, Dvergr wood door, hexagonal gate, flametal gate and ashwood door. Native prefab/component checks select the exact supported definitions in the [specification](../specs/door-keeper.md).

Only a door whose delay has elapsed and whose doorway is clear requests surrounding terrain and collision context. Preparation rotates one door per pass; a large or unready context postpones closure. Server ownership lasts only for the close attempt and is then released. Manual operation retains native interaction and ward/access checks. A restart begins a fresh delay for doors left open.

Terrain compilers that were created before their heightmap was available resume native initialization when ready. Incomplete terrain state postpones closing. Debug eligibility summaries identify compiler readiness blockers when present.

## Configuration

Use `[DoorKeeper]` in `heimkit.cfg`. This feature defaults enabled; set Enabled=false to disable it. Stop the server before editing and restart to apply settings. HeimKit preserves existing values and supplies missing defaults.

| Setting | Default | Purpose |
| --- | --- | --- |
| Enabled | true | Enable DoorKeeper. |
| DebugMode | false | Enable feature diagnostics through the shared debug policy. |
| CloseDelaySeconds | 5 | Continuous observed open time before closing. |
| ClearanceMeters | 3 | Minimum occupancy sphere radius; native gate geometry can enlarge it. |
| ScanRangeMeters | 64 | Range around a rotating ready player. |
| IntervalSeconds | 2 | Processing interval; existing configured values are preserved. |

Ranges and selection rules are in the [specification](../specs/door-keeper.md). DebugMode follows the [shared debug/suppression policy](../server-setup.md#diagnostics).

## Diagnostics

Normal output groups confirmed results per completed pass and preserves failures and lifecycle notices. Optional Debug summaries describe changed current scope and result counts at most once per minute. `scope_now` counts tracked open doors; `results_pass` counts completed operations in the latest pass; `results_total` counts them during the current controller lifetime. Counts restart when the controller is recreated. Empty unchanged scopes are quiet. Eligibility summaries include context_ready, delay_wait_pass and occupied_pass. Wide/tall doors use a larger native geometry-based occupancy sphere.

See [shared safety and delivery limitations](../limitations.md#new-world-features) and [server setup](../server-setup.md).

Discovery reuses nearby candidate records and accepts native record changes. Entering another area seeds discovery again; action eligibility and timing retain the configured rules. See the [shared discovery design](../architecture.md#record-driven-candidate-discovery).
