# Infinite torches specification

## Status and scope

Implemented in `src/HeimKit/Features/InfiniteTorches/InfiniteTorches.cs`. The project builds against Windows Valheim 1.0.17 and BepInEx 5. Automated lifecycle/configuration checks pass. An initial live wooden-torch refill test passed; broader multiplayer acceptance remains pending.

### Live evidence: October 10, 2026

Trevor supplied Windows dedicated-server logs showing Valheim 1.0.17, BepInEx 5.4.23.5, HeimKit 0.1.0 and debug mode enabled. The plugin found 52 eligible torches, sent 32 requests followed by 20, and logged wooden torch fuel targets of 4. Trevor confirmed that empty torches lit up and displayed 4/4 fuel.

This verifies observed wooden-torch refilling and lighting in that session. It does not establish all prefab types, zero inventory consumption, clean-client status, two-client consistency, ownership transfer, restart behavior or Linux support. Keep those acceptance checks pending.

Goal: automatically refill eligible torches for free on a dedicated server, using unmodified clients. This is periodic refilling, not a promise of continuous light under every environmental condition.

Follow [the architecture rules](../architecture.md) for naming, placement, shared code and module independence.

## Network mechanism

The server holds and distributes shared fuel state, while the object's simulation owner calculates fuel consumption. The module inspects ZDO records in connected players' near simulation sectors and sends the existing vanilla `RPC_SetFuelAmount(float)` message to the object's current owner.

Inspection of the installed client/server Fireplace classes confirmed matching code, float RPC registration and owner-side fuel writes. The implementation does not introduce custom client RPC handlers, claim ownership or directly overwrite remote-owned ZDO fuel data.

An unloaded object or ownership transfer can cause a message to be ignored; subsequent passes retry. Logs count requests, not acknowledgements.

## Configuration defaults

| Section | Setting | Default | Rules |
| --- | --- | --- | --- |
| Torches | Enabled | true | Independent on/off switch; edits require restart. |
| Torches | IntervalSeconds | 5 | Range 2–60. |
| Torches | MaxMessagesPerPass | 32 | Range 1–256. |
| Torches | Prefabs | See list below | Comma-separated runtime-checked candidates. |
| Diagnostics | DebugMode | false | Master switch for all troubleshooting diagnostics. |

Default prefab names:

```text
piece_groundtorch_wood
piece_groundtorch
piece_groundtorch_green
piece_groundtorch_blue
piece_walltorch
```

All settings are bound before `ConfigurationLoader` saves `heimkit.cfg` in BepInEx's runtime config directory. The example config is optional. Existing values are preserved; missing defaults are added.

## Eligibility and behavior

1. Initialize only when enabled and game version is 1.0.17.
2. Wait for dedicated-server networking and world services.
3. Resolve candidate prefab definitions; warn and skip missing or unsupported entries.
4. Require refillable Fireplace data, finite positive maximum fuel, no inherent infinite-fuel flag, no toggle-off behavior, and no wet low/high flame objects.
5. Read connected ready peers with character IDs and their near simulation sectors.
6. Select valid candidate ZDOs owned by that peer, deduplicating records.
7. Skip torches whose ordinary state is off or whose fuel is not finite.
8. Below 75% normal capacity, request refill to that capacity without spending player inventory materials.
9. Limit requests per pass and rotate the candidate cursor.
   Track pending requests by ZDOID across players. Suppress additional sends for the same object until sufficient replicated fuel progress, owner change, object removal or a 30-second timeout. Reconcile all pending objects, including those outside scanned player areas. Cap tracking at 2,048 entries; capacity exhaustion defers new sends. Remove the reservation if routing throws, and clear tracking on initialization/shutdown. Fuel progress requires a finite increase above the prior value and at least 75% of target fuel.
10. Disable the module and report an error if its runtime processing fails.

Disabled modules do no world work. Shutdown clears caches and scheduling state and is safe to repeat.

## Logs

- Config creation/loading reports the exact active path.
- Startup reports `Infinite Torches: ENABLED` with interval, message budget and diagnostics state, or `DISABLED` with a reason.
- Prefab resolution reports readiness and eligible count, with warnings for skipped candidates.
- Diagnostic messages report sent requests and candidate counts, explicitly stating that client application is unconfirmed.
- TorchDiagnostics.cs uses one DebugMode switch. Status reports and detailed-pass samples are each automatically limited to 60 seconds; detailed samples are also bounded by the request budget. Request counts log on every sending pass when debug mode is on.
- Status explains missing world services, wrong session, unsupported prefab configuration, no ready players, no nearby eligible client-owned torches, sufficient fuel, skip counts and deferred candidates. Counts describe the current pass; unexamined candidates are deferred, not classified as sufficiently fueled.
- Sampled requests identify object ID, prefab, owner, position, fuel before the request, target and RPC. They are transmission attempts, not delivery acknowledgements.
- Prefab warnings identify rejection reasons; runtime failures include operation context.
- Errors identify the module and disable it.
- Debug mode reports observed fuel increases, timeouts, owner changes and removed objects. Pass summaries include pending count, suppressed duplicates and tracking-capacity skips. These observations are not RPC acknowledgements. Version 0.1.1 identifies this change; live evidence above applies to 0.1.0.

## Limits

Vanilla smoke, obstruction and water behavior remains. Other fire types are outside the default allowlist. Newly loaded empty torches may remain dark briefly. Normal refill effects may appear. A simultaneous manual refill can still consume player resin. The message budget does not bound sector scanning cost, so large bases need performance testing.

No Linux support is advertised. Crossplay behavior and ownership transitions need live tests. Preserve the compatibility guard until a new version has been reviewed.

## Acceptance

Completed automated checks:

- [x] Builds against the inspected local references.
- [x] Enabled/disabled and supported/unsupported version states log accurately.
- [x] Repeated shutdown leaves the module inactive.
- [x] Missing config directory/file is created with every default.
- [x] Existing values are preserved and missing settings added.
- [x] Config setup failures report errors instead of successful creation.
- [x] Optional config defaults the single DebugMode switch off.
- [x] Status logging respects its interval and off switch without formatting suppressed messages.
- [x] Detailed-pass sampling respects its single debug switch, interval and reset.
- [x] Pending-request checks cover duplicate IDs, delayed/partial fuel progress, timeout retries, ownership changes, object removal, bounded memory and shutdown cleanup.

Pending live tests:

- [ ] Clean vanilla clients connect to the Windows dedicated server.
- [ ] Empty/low-fuel eligible torches reach normal maximum without consuming resin.
- [ ] Two clients see matching light and fuel state.
- [ ] Ownership transfers, disconnects and unload/reload preserve eventual refilling.
- [ ] Server restart resumes refilling with ordinary saved state.
- [ ] Disabling the switch stops refills after restart.
- [ ] Vanilla environment rules and unrelated fires retain their behavior.
- [ ] Large groups progress with acceptable server performance.
- [ ] Removal leaves ordinary usable objects and fuel state.
- [ ] Steam/crossplay are checked separately before claiming both.

Update this spec with observed results and versions when tests are performed. Do not infer successful gameplay from compilation or a sent-request log.

DebugMode defaults off and suppresses request counts, status explanations and detailed samples through one switch. Standard startup, configuration, readiness, warnings and errors remain available. Enable DebugMode and restart to troubleshoot. Diagnostic messages use Info level for visibility in normal BepInEx logs.

Existing DebugMode values are preserved. Retired per-category diagnostic settings are removed during configuration binding before save.
