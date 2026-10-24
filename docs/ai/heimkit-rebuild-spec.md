# HeimKit performance rebuild — implementation specification

> **Status:** authorized design specification for a from-scratch rebuild of HeimKit into a
> new `src/HeimKit2/` tree. This document is the single source of truth for *where code lives*
> and *what the shared core owns*. If a placement is not answered here, stop and ask the
> repository owner (Trevor) rather than guessing.

## 0. How to read this document

You are an AI agent implementing a greenfield rebuild. The existing `src/HeimKit/` plugin
stays in place, untouched, as the **behavioral reference** you port from. You build a parallel
plugin in `src/HeimKit2/`.

The central goal is **de-duplication for performance**: today each feature independently scans
the world, classifies prefabs, caches native reflection, and re-runs world-readiness checks.
In the rebuild those responsibilities move into one **Core** layer that every feature consumes.
A feature must never re-implement a Core responsibility.

Read, in order, before writing any code:

1. This document (authority on placement and the Core contracts).
2. `AGENTS.md` (repository rules; still apply in full to `src/HeimKit2/`).
3. `docs/architecture.md` (the *current* architecture — carry forward its lifecycle, logging,
   configuration and player-service contracts; this document only changes *code placement* and
   *shared ownership*, not those behavioral contracts).
4. The feature spec under `docs/specs/<feature>.md` for whichever feature you are porting.
5. The current implementation under `src/HeimKit/Features/<feature>/` for exact behavior.

**Behavioral rule:** The rebuilt HeimKit must be functionally identical to the legacy implementation from a player/operator
point of view. Same features, same settings keys, same config file name (`heimkit.cfg`), same
gameplay effects. Approved logging exceptions are timestamped topic-based output, rewritten Debug/Error records, the shared DebugVerbosity setting (Normal by default), and WeatherGuard ten-minute prevention roll-ups; other Info/Warning wording remains unchanged. Performance and internal structure change; observable
behavior does not. Where current behavior is subtle (ZDO ownership transfer, Harmony IL
transpilers, replication fences), preserve it exactly — port, do not reinvent the logic.

## 1. Ground rules (non-negotiable)

- **Target platform:** Windows Valheim dedicated servers only. Same `[BepInProcess("valheim_server.exe")]` guard.
- **Do not build or publish.** Per `AGENTS.md`, do not compile, package, bump versions, or create
  release artifacts unless Trevor explicitly asks. Leave all edits local and reviewable.
- **Do not touch `src/HeimKit/`.** It remains the live, working plugin and your reference. No edits there.
- **One HeimKit implementation runs on a server.** The rebuild replaces the legacy plugin and retains the `HeimKit` product name, `heimkit` BepInEx GUID, `HeimKit.dll` assembly, `HeimKit` namespaces and `heimkit.<feature>` Harmony IDs. Temporary source/test directories distinguish the implementations in the repository; only one DLL is deployed. Do not declare incompatibility with the plugin's own GUID or reject its own loader registration.
- **Server-only compatibility discipline** from `docs/architecture.md#server-only-compatibility`
  applies unchanged. Do not change the audited game version.
- **Testing stays in chat.** Do not write test results, validation status, or testing instructions
  into any committed file (`AGENTS.md` chat-only rule). Report those to Trevor in conversation.

## 2. Target directory layout

The temporary source directory is `src/HeimKit2/`; it becomes `src/HeimKit/` when Trevor authorizes the cutover. Product, project and namespace identity already remains HeimKit. Create this tree with namespaces rooted at: `HeimKit`, `HeimKit.Core.<Area>`,
`HeimKit.Features.<Name>`.

```text
src/HeimKit2/
  HeimKit.csproj                 # new project; mirror HeimKit.csproj references (BepInEx, Harmony, game asms)
  Plugin.cs                       # entry point: config load, module registration, tick dispatch, shutdown
  Core/
    Scheduling/
      IServerModule.cs            # module contract (see §4.1)
      ModuleScheduler.cs          # owns per-module interval + startup phase stagger (see §3, §4.1)
      ModuleHost.cs               # try/catch isolation, cleanup dispatch (replaces Plugin's inline loop)
    World/
      WorldObjectProvider.cs      # THE single shared world scan (see §4.2) — replaces per-feature scanning
      WorldCandidateIndex.cs      # spatial index (ported from Shared/WorldCandidateIndex.cs)
      WorldRecordChanges.cs       # ZDO change fan-out (ported from Shared/WorldRecordChanges.cs)
      CandidateSubscription.cs    # a feature's filtered, read-only view onto the provider (see §4.2)
      PlayerWorldScope.cs         # actor sampling + movement segments (ported from Shared/PlayerWorldScope.cs)
      TerrainContext.cs           # ported from Shared/WorldTerrainContext.cs
      NativeWorldObject.cs        # ported from Shared/NativeWorldObject.cs
    Prefabs/
      PrefabRegistry.cs           # THE single prefab classification cache (see §4.3)
      PrefabCollisionBounds.cs    # ported from Shared/PrefabCollisionBounds.cs
    Native/
      NativeAccess.cs             # THE single native field/method accessor cache (see §4.4)
      NativeCharacterHealth.cs    # ported from Shared/NativeCharacterHealth.cs
    Ownership/
      ObjectAuthority.cs          # ported/merged from Shared/ServerObjectAuthority.cs (see §4.5)
      ZdoPackets.cs               # ported from Shared/NativeZdoPackets.cs
    Players/                      # ported wholesale from Shared/Players/ (see §4.6) — keep file names
      ...
    Diagnostics/
      DiagnosticLog.cs            # ported from Shared/DiagnosticLog.cs
      LogTopic.cs
      DiagnosticBudget.cs
      SummaryCadence.cs
      ProcessingTiming.cs
      DebugLoggingDelivery.cs
      FeatureDiagnostics.cs       # NEW shared base for feature Diagnostics classes (see §4.7)
    Lifecycle/
      ModuleCleanup.cs            # ported from Shared/ModuleCleanup.cs
      ServerDebugActivity.cs      # ported from Shared/ServerDebugActivity.cs
  Configuration/                  # ported wholesale from src/HeimKit/Configuration/ (keep file names)
    ConfigurationLoader.cs
    ConfigurationLayout.cs
    GameVersionCompatibility.cs
    DebugLoggingPolicy.cs
    DebugLoggingResolver.cs
    DiagnosticsSettings.cs
    PlayerActivitySettings.cs
  Features/
    InfiniteTorches/
    WeatherGuard/
    AFKNightWatch/
    AFKRaidWatch/
    SharedMap/
    DoorKeeper/
    ForestRenewal/
    SeaSalvage/
    TameTravel/
    HeimMender/
```

Rules for this tree:

- **`Core/` is the only place shared code lives.** Anything used by two or more features, or that
  is an application-wide contract, lives under `Core/` in the matching area. Nothing shared lives
  under `Features/`.
- **`Features/<Name>/` holds only that feature's own logic**: its `Settings` class, its main
  lifecycle class (`<Name>`), its controller(s), its `Diagnostics` class, and a `Models/` folder
  for its own data types. A feature folder must not contain a world scanner, a prefab classifier,
  a native-reflection cache, an ownership/packet fence, a presence/activity hub, or a config loader.
  Those are Core.
- **Naming** follows `docs/architecture.md#naming` unchanged: no class/interface contains the word
  `Feature`; feature-owned classes are `<FeatureName><Responsibility>`; namespaces are
  `HeimKit.Features.<Name>`, `HeimKit.Core.<Area>`, `HeimKit.Configuration`.

## 3. Performance requirements (the reason for the rebuild)

Every one of these is a hard acceptance criterion. See §10.

1. **One world scan per frame, shared.** Across the whole plugin, native sector enumeration
   (`ZDOMan.FindSectorObjects`) for player-proximate objects happens in **one** place —
   `WorldObjectProvider` — at most once per distinct player sector per scan cycle. Features obtain
   candidates through a filtered `CandidateSubscription`, never by calling `FindSectorObjects`
   themselves and never by constructing their own `WorldCandidateIndex`. (Today InfiniteTorches,
   WeatherGuard, SharedMap, DoorKeeper, ForestRenewal, SeaSalvage and HeimMender each scan
   independently over overlapping sectors.)
2. **No reflection in hot paths.** All native field access uses cached delegates / `AccessTools.FieldRefAccess`
   obtained once at construction via `NativeAccess`. `FieldInfo.GetValue`/`SetValue` and
   `MethodInfo.Invoke` must not appear in any per-frame Harmony patch or per-tick loop. (Today
   `WeatherGuardController.SuppressWeather` boxes a bool via `FieldInfo.GetValue` on every protected
   piece's wear sweep.)
3. **One prefab classification cache, shared.** Component lookups (`GetComponent<T>()`,
   `GetComponentsInChildren<T>()`) against a prefab happen once per prefab hash, in `PrefabRegistry`,
   and are cached. Features query the registry; they do not keep their own `Dictionary<int,...>`
   of prefab metadata. (Today WeatherGuard `_eligibility`, SeaSalvage `_definitions`, DoorKeeper
   `_definitions`/`_doorBounds`, ForestRenewal `_definitions`, InfiniteTorches `_torches`,
   TerrainContext `_terrainDefinitions` are separate caches doing overlapping `GetComponent` work.)
4. **Key by `ZDOID`, never `ZDOID.ToString()`.** No per-object string keys in any map or set that
   is rebuilt or iterated per tick. `ZDOID` is a struct with a usable `GetHashCode`. String
   identities are produced only when a diagnostic line is actually emitted.
5. **No per-tick allocation in steady state.** Reusable buffers (lists, sets, dictionaries) are
   fields cleared and refilled, not reallocated each tick. LINQ (`Where`/`Select`/`Any`/`ToArray`/
   `ToList`) is forbidden in any per-tick or per-frame path; use explicit loops and reused removal
   lists. Deferred diagnostic lambdas must not allocate closures on a path that runs when debug is
   off — gate the closure creation behind the debug check.
6. **Staggered ticks.** `ModuleScheduler` assigns each module a small fixed startup phase offset so
   modules sharing an interval do not land their heavy passes on the same frame. Interval values
   themselves stay as configured.
7. **Diagnostics never cost when off.** Status snapshot objects and their dictionary copies are
   built only when a summary will actually emit (debug enabled and cadence due), never
   unconditionally per tick.

These requirements are architectural, not optional micro-optimizations. A feature port that
re-introduces an independent scan, an independent prefab cache, or hot-path reflection is incorrect
even if it compiles and behaves correctly.

## 4. The Core layer — contracts

This section defines each Core service: what it owns, its public surface, what feature code it
replaces, and the rules features follow when using it. Where a file is "ported", copy the current
behavior verbatim into the new namespace/folder, then wire it to the new Core consumers; do not
redesign ported logic beyond the namespace move and the de-duplication wiring named here.

### 4.1 Scheduling — `Core/Scheduling/`

**`IServerModule`** (ported contract, unchanged shape):

```csharp
namespace HeimKit.Core.Scheduling;
public interface IServerModule
{
    string Name { get; }
    bool IsActive { get; }
    void Initialize(string gameVersion);
    void Tick(float now);     // now == Time.unscaledTime
    void Shutdown();
}
```

**`ModuleScheduler`** owns tick cadence and the §3.6 stagger. A module no longer stores its own
`_nextTick`/`_next` float and compares `now < _next`. Instead it declares its interval, and the
scheduler calls its work callback only when due, passing a phase offset at registration so modules
do not synchronize. The interval source stays each feature's own settings value.

**`ModuleHost`** owns the per-module `try/catch` isolation and cleanup dispatch that
`Plugin.Update`/`Awake` do today (`ModuleCleanup.Run` on failure, disable-and-report semantics).
`Plugin.cs` becomes thin: load config, register modules, call `ModuleHost.Tick`, shut down.

Rules:
- Every feature implements `IServerModule`. Lifecycle/isolation rules from
  `docs/architecture.md#module-lifecycle-and-isolation` are unchanged and enforced by `ModuleHost`.
- World-readiness gating (the repeated `ZNet/ZNetScene/ZDOMan/ZRoutedRpc == null`, `IsServer`,
  `IsDedicated` checks every controller does today) is provided once by Core (see §4.2
  `WorldObjectProvider.Ready` / a `WorldServices` readiness snapshot). Features read the shared
  readiness result; they do not re-null-check native singletons in their own Tick.

### 4.2 World scanning — `Core/World/`

This is the single most important de-duplication. **`WorldObjectProvider`** is the only component
that enumerates world objects around players.

Responsibilities it owns:
- Sampling ready, joined, living player positions (via `PlayerWorldScope`, which it owns).
- Maintaining the shared `WorldCandidateIndex` and the `WorldRecordChanges` change feed.
- Running native `FindSectorObjects` at most once per distinct player sector per scan cycle,
  deduplicated across all subscribers.
- Exposing results to features as filtered, read-only `CandidateSubscription` views.

Public surface (shape, not final signatures — refine against the ported index):

```csharp
namespace HeimKit.Core.World;
internal sealed class WorldObjectProvider : IDisposable
{
    bool Ready { get; }                 // native services present + dedicated server + world loaded
    IReadOnlyList<Vector3> PlayerPositions { get; }
    PlayerWorldScope Scope { get; }     // movement segments, PlayerNear, InRange, Center rotation

    // A feature registers ONE subscription with its accept-filter. The provider classifies each
    // discovered record once; every matching subscriber receives it without a second native query.
    CandidateSubscription Subscribe(string owner, Func<ZDO, bool> accept, ScanKind kind);

    void Tick(float now);               // called by Plugin/ModuleHost before feature ticks
}

internal sealed class CandidateSubscription : IDisposable
{
    IReadOnlyList<ZDO> Records { get; } // current-pass matches, owned by the provider, do not mutate
    // optional per-feature spatial narrowing already handled by the shared index
}
```

Rules for features:
- A feature obtains world objects **only** through a `CandidateSubscription`. It must not call
  `ZDOMan.FindSectorObjects`, must not construct `WorldCandidateIndex`, and must not keep its own
  sector lists seeded from native scans.
- The `accept` filter must be a cheap predicate over a `ZDO` (prefab-hash membership, persistence,
  owner). Expensive classification goes through `PrefabRegistry` (§4.3), whose results the provider
  caches, so two features filtering on the same prefab do not each pay for `GetComponent`.
- `ScanKind` captures the distinctions features need today: near-only vs near+distant, simulation
  distance source (per-peer `m_simulationDistance` vs synced server distance), classic flag. The
  provider computes the **union** of requested radii per sector so one native query serves all.
- Features that need movement-aware proximity (`PlayerNear`, segment sweep) read `Scope`; they do
  not duplicate the segment math.
- `WeatherGuard` is the key migration: today it owns a bespoke per-second rescan
  (`_near`/`_distant`/`_regions`, its own `FindSectorObjects`, its own `AddInstances` reflection on
  `ZNetScene.m_instances`). In HeimKit it consumes a `CandidateSubscription` like every other
  feature. Its instance-retention trick (keeping live nonpersistent drops between scans via
  `m_instances`) becomes a Core capability on the provider (an "include live instances in active
  area" option on the subscription), because it is a general concern, not a WeatherGuard one.

### 4.3 Prefab classification — `Core/Prefabs/PrefabRegistry.cs`

One cache mapping a prefab hash to its classified metadata. Classification runs once per hash and is
memoized. Features ask the registry questions; they never call `GetComponent` on a prefab in their
own code and never keep their own prefab→metadata dictionary.

The registry exposes the union of what features need today. Model it as a single cached record per
prefab, lazily computed:

```csharp
namespace HeimKit.Core.Prefabs;
internal readonly struct PrefabInfo
{
    string Name { get; }
    bool HasWear, StaticWear, NoRoofWear, HasPiece, HasContainer, HasShieldGenerator;
    bool HasTerrainComp, HasTerrainModifier, HasFloating, HasFish, HasCharacter, HasItemDrop, HasTombStone;
    int ColliderCount, RigidbodyCount;
    float WearHealth;
    Piece.PieceCategory PieceCategory;
    // door/fireplace/dungeon specifics as needed by their features (see per-feature notes)
}
internal sealed class PrefabRegistry
{
    PrefabInfo Get(int prefabHash);     // memoized; null-prefab handled explicitly
    bool TryGet(int prefabHash, out PrefabInfo info);
}
```

Rules:
- The registry is the only caller of `GetComponent`/`GetComponentsInChildren` against prefabs (plus
  `PrefabCollisionBounds`, which it may own or call). Feature-specific *eligibility logic* (e.g.
  WeatherGuard's `Exclusion`/`SelectionReason`, DoorKeeper's "is a closeable door") stays in the
  feature's Rules class, but it is computed *from* `PrefabInfo`, not from fresh component lookups.
- Cache capacity and eviction mirror the current per-feature caps (e.g. WeatherGuard caps at 4096);
  apply one shared bound.
- Door collision bounds and fireplace component references that a feature needs as live objects
  (e.g. InfiniteTorches holds a `Fireplace` component; DoorKeeper needs door bounds) are exposed by
  the registry as typed accessors so the component lookup still happens once.

### 4.4 Native member access — `Core/Native/NativeAccess.cs`

One place that resolves and caches native fields and methods as fast delegates. Resolved once at
startup; validated once (fail fast with a clear `MissingMemberException` if a member is absent,
exactly as `WeatherGuardController.Install` does today).

- Resolve fields through Core's runtime-compatible cached generator: Harmony field references on Unity Mono, and direct DynamicMethod emission on modern .NET. Never use raw ref-return DynamicMethod construction on Mono.
- Fields become `AccessTools.FieldRefAccess<TInstance,TField>` ref-getters (direct access, no
  boxing), **not** `FieldInfo`. This covers every `FieldInfo` currently held by WeatherGuard
  (`m_rainWet`, `m_haveRoof`, `m_shieldChangeID`, `m_rainTimer`, `m_createTime`,
  `m_updateCoverTimer`, `Piece.m_creator`, `Piece.m_creatorPlatformUserIDIndex`,
  `ZNetScene.m_instances`) and TerrainContext (`TerrainComp.m_initialized`, `m_hmap`, the array
  fields).
- Methods become typed delegates via `CreateDelegate` (as WeatherGuard already does for
  `ShouldUpdate`, `IsUnderWater`, `CanBeRemoved`, `CreateLocalZones`), centralized here.
- Harmony patch *target* resolution (exact signatures, `AccessTools.Method`) may stay in each
  feature's `Patches` class, but the shared signature-resolution helper pattern from
  `ServerObjectAuthority.NativePatches` lives in Core so features resolve targets consistently.

Rule: no feature declares its own `FieldInfo`/`MethodInfo` field. If a feature needs a native
member not yet in `NativeAccess`, it adds it to `NativeAccess`.

### 4.5 Ownership & packets — `Core/Ownership/`

`ObjectAuthority` (from `ServerObjectAuthority`) and `ZdoPackets` (from `NativeZdoPackets`) become
the single owner of: retained native-record ownership, packet provenance/parsing, stale-data and
destruction fencing, and release. Today DoorKeeper and SeaSalvage share these, but WeatherGuard and
TameTravel carry parallel ownership/packet logic. In HeimKit:

- All four (WeatherGuard, TameTravel, DoorKeeper, SeaSalvage) use `ObjectAuthority`/`ZdoPackets`.
- Where a feature has a genuinely distinct fence (TameTravel's traversal/animal transaction fence
  differs from static-object retention, per `docs/architecture.md#native-world-operations`), that
  feature keeps its *feature-specific transaction type* in its own folder, but it is built on the
  Core packet/ownership primitives, not a reimplementation of packet parsing.
- The per-operation incident reporting and bounded shared Error handling stay in Core.

### 4.6 Players — `Core/Players/`

Port the entire `Shared/Players/` subtree with file names preserved. This subsystem
(`PlayerServices`, `PlayerPresence`, `PlayerActivityService` and its tracker/notices/diagnostics,
`PlayerRoutedMessages`, `RoutedActivityReader`, `PlayerConnectionNotices`, `PlayerProcessingNotices`,
`PlayerPresenceEvents`, `PlayerWorldScope` → moved to `Core/World/`, `PlayerPeerLookup`,
`PlayerEquipment`, the `Models/` and interfaces) already is the shared hub and already follows the
contracts in `docs/architecture.md#player-activity`. Those contracts are unchanged.

Rules (unchanged from current architecture, restated so they are not lost):
- `PlayerServices` is constructed once in `Plugin.cs` and injected into every feature constructor.
  No feature takes its own peer census or infers population independently.
- Presence-driven lifecycle, activity leases, routed-message observer ownership and the empty-server
  debug pause work exactly as documented today.

### 4.7 Diagnostics — `Core/Diagnostics/`

Port `DiagnosticLog`, `LogTopic`, `DiagnosticBudget`, `SummaryCadence`, `ProcessingTiming`,
`DebugLoggingDelivery`. Add **`FeatureDiagnostics`**, a shared base that captures the pattern every
feature's `Diagnostics` class repeats: debug-policy gating, the `Report`/`Record` deferred-lambda
entry points, budget/cadence wiring, and the §3.7 "build the status object only when it will emit"
discipline. Each feature's `Diagnostics` class derives from it and adds only feature-specific
message selection and evidence formatting.

The logging contract in `docs/architecture.md#logging` governs timestamped topic output, grouping, repetition limits and the empty-server pause. `FeatureDiagnostics` preserves the existing gates, stream budgets and incident identities.

## 5. The anti-duplication decision table

This is the "no guessing" reference. When a feature needs capability X, it uses the Core service
named here. It must not implement its own.

| A feature needs to… | It uses… | It must NOT… |
| --- | --- | --- |
| Find world objects near players | `Core/World/WorldObjectProvider` via a `CandidateSubscription` | call `ZDOMan.FindSectorObjects`, build a `WorldCandidateIndex`, or keep its own sector lists |
| Know player positions / proximity / movement | `WorldObjectProvider.Scope` (`PlayerWorldScope`) | re-sample peers or re-derive movement segments |
| Classify a prefab (components, category, health, colliders) | `Core/Prefabs/PrefabRegistry` | call `GetComponent`/`GetComponentsInChildren` on a prefab, or keep a prefab→metadata dictionary |
| Read/write a native private field or call a native private method | `Core/Native/NativeAccess` (FieldRef/delegate) | declare `FieldInfo`/`MethodInfo` fields or call `GetValue`/`Invoke` in hot paths |
| Retain/release ZDO ownership, parse/fence packets | `Core/Ownership/ObjectAuthority` + `ZdoPackets` | re-implement packet parsing, ownership transfer, or destruction fencing |
| Prepare terrain / create persistent world objects | `Core/World/TerrainContext` + `NativeWorldObject` | re-resolve terrain compiler reflection or re-implement zone creation |
| Check living/dead/finite actor health | `Core/Native/NativeCharacterHealth` | read health fields directly for eligibility |
| Know who is connected / joined / AFK; get presence events | `Core/Players/PlayerServices` (injected) | take a peer census, parse logs, or infer population |
| Observe authenticated routed RPCs (chat, emote, damage, container) | `Core/Players/PlayerRoutedMessages` + `RoutedActivityReader` | install its own routed observer |
| Schedule periodic work | `Core/Scheduling/ModuleScheduler` (declared interval + phase) | store its own `_next` float and compare `now` |
| Gate on world-service readiness | shared readiness from `WorldObjectProvider`/`WorldServices` | re-null-check `ZNet`/`ZNetScene`/`ZDOMan`/`ZRoutedRpc` in its own Tick |
| Emit logs / summaries / debug evidence | `Core/Diagnostics/FeatureDiagnostics` base + `DiagnosticLog` | invent its own log formatting or budgeting |
| Load/define settings | own `Settings` class bound through `Core`/`Configuration/ConfigurationLoader` | create a second config file or its own loader |
| Clean up on failure/shutdown | `Core/Lifecycle/ModuleCleanup` | swallow cleanup errors or skip remaining steps |
| Drain and hand off an existing native object with ordered replication | `Core/Ownership/ObjectTransferAuthority`, `OwnershipLease` and `ZdoReplication` | duplicate source-drain, owner fencing or native peer bookkeeping |
| Compare native object identities without allocating strings | `Core/Native/NativeIdentity` | create object-ID strings inside repeated sorting/comparison loops |

**Rule for new capabilities:** if a feature needs something not in this table and it is plausibly
useful to a second feature, add it to Core and extend this table in the same change. If it is truly
unique to one feature, it lives in that feature's folder. When unsure, ask Trevor — do not create a
second copy of an existing Core responsibility.

## 6. Feature folder contract

Each `Features/<Name>/` contains, and contains only:

- `<Name>.cs` — the main class implementing `IServerModule`: settings construction, enable/compat
  checks, lifecycle, presence subscription, startup state log, failure isolation. (Per
  `docs/architecture.md`, the main class owns lifecycle; the controller owns gameplay processing.)
- `<Name>Settings.cs` — its config section and keys (same keys as HeimKit today).
- `<Name>Controller.cs` (and additional responsibility classes as needed, named
  `<Name><Responsibility>`) — gameplay processing that consumes Core services.
- `<Name>Diagnostics.cs` — derives from `Core/Diagnostics/FeatureDiagnostics`.
- `<Name>Rules.cs` — pure decision logic (eligibility, selection, routing) computed from Core data.
- `Models/` — the feature's own data types/enums only.
- `<Name>Patches.cs` — Harmony patches the feature owns, using `NativeAccess` for member access and
  resolving targets with the shared signature helper.

A feature folder must not contain: a world scanner, a prefab-metadata dictionary, a
`FieldInfo`/`MethodInfo` cache, a packet parser, an ownership manager, a presence/activity hub, a
config loader, or a second copy of any Core diagnostic/budget utility.

## 7. Per-feature porting notes

Port in the order in §8. For each feature: read its `docs/specs/<feature>.md` and current
`src/HeimKit/Features/<feature>/`, reproduce behavior exactly, and route every capability in §5
through Core. Feature-specific notes:

- **InfiniteTorches** — already uses `WorldCandidateIndex`; in HeimKit it uses a
  `CandidateSubscription` instead. Keep `InfiniteTorchesRefillRules`, pending-refill tracking,
  per-pass message cap, and the `RPC_SetFuelAmount` routed request. Fireplace component lookups go
  through `PrefabRegistry`.
- **WeatherGuard** — the largest port. Replace the bespoke rescan + `m_instances` reflection with a
  `CandidateSubscription` (including the "retain live instances in active area" provider option).
  Replace all `FieldInfo.GetValue` with `NativeAccess` FieldRefs (§3.2, §4.4). Keep the
  `UpdateWear` IL transpiler (`WeatherDamage`/`WeatherContribution`) and all Harmony patches exactly
  — port the audited IL-matching logic verbatim. Eligibility (`Exclusion`/`SelectionReason`) stays
  in `WeatherGuardRules`, computed from `PrefabInfo`. Ownership goes through `ObjectAuthority`.
  Network filtering (`WeatherGuardNetworkFilter`) stays feature-owned but uses `ZdoPackets`
  primitives. Rain/exposure evidence maps key by `ZDOID` (§3.4).
- **AFKNightWatch / AFKRaidWatch** — mostly player-activity consumers; port their sleep-vote and
  raid-candidate hooks, leasing activity through `PlayerServices` exactly as documented. No world
  scanning. Keep their feature-specific diagnostics contracts (`docs/specs/afk-*.md`).
- **SharedMap** — the biggest consumer of discovery. Its portal/resource/dungeon/runestone/spawner
  discovery, depletion observation, grouping/publication, table init and map-field filtering all
  consume `WorldObjectProvider` subscriptions and `PrefabRegistry`. Keep its stores, clustering, and
  save-management (dirty terrain/discovery/publication-state saves) feature-owned; those are its
  exempt offline responsibilities. Its dungeon/landmark prefab classification goes through
  `PrefabRegistry` extensions.
- **DoorKeeper / SeaSalvage** — already share `PlayerWorldScope`/`ObjectAuthority`/`ZdoPackets`;
  straightforward re-wire to Core. Door/salvage prefab definitions move into `PrefabRegistry`.
- **ForestRenewal** — consumes `WorldObjectProvider`; keep its durable ledger save-management
  (background snapshot + acknowledged generation gate) feature-owned (exempt save responsibility).
- **TameTravel** — port its traversal/animal transaction fence on top of `ObjectAuthority`/
  `ZdoPackets`; keep its follow hooks and routed-input observation via `PlayerRoutedMessages`. De-LINQ
  its per-tick paths.
- **HeimMender** — repair selection/batches, its section-only live config observer (its one exempt
  responsibility). World candidates via subscription; repair targets classified via `PrefabRegistry`.

## 8. Build order & milestones

Do these as separate, reviewable steps. Do **not** compile/build unless Trevor asks; "milestone
complete" means the code is written and self-consistent, with behavior verified in chat with Trevor.

1. **M0 — Project skeleton.** `HeimKit.csproj`, `Plugin.cs` shell, `Core/Scheduling/` (IServerModule,
   ModuleHost, ModuleScheduler), `Configuration/` ported, GUID `heimkit`. No features yet.
2. **M1 — Core foundation.** Port `Core/Players/`, `Core/Diagnostics/`, `Core/Lifecycle/`,
   `Core/Native/`, `Core/Prefabs/`, `Core/World/` (including `WorldObjectProvider`,
   `CandidateSubscription`, ported index/changes/scope/terrain), `Core/Ownership/`. This is the bulk
   of the value; get the shared contracts right before any feature depends on them.
3. **M2 — First consumer.** Port **InfiniteTorches** end-to-end as the reference feature that proves
   the `CandidateSubscription` + `PrefabRegistry` + `ModuleScheduler` pattern. Use it as the template.
4. **M3 — World-heavy features.** DoorKeeper, SeaSalvage, ForestRenewal, HeimMender (share the most
   with Core world/ownership).
5. **M4 — WeatherGuard.** The hardest; do it alone after the Core world/instance-retention and
   `NativeAccess` FieldRefs are proven.
6. **M5 — SharedMap.** Large discovery surface; many `PrefabRegistry`/subscription extensions.
7. **M6 — Player-activity features.** AFKNightWatch, AFKRaidWatch, TameTravel.
8. **M7 — Documentation.** §9.

Each milestone: port behavior, route through Core, confirm the §3 performance requirements hold for
the ported feature, and report to Trevor in chat.

## 9. Documentation updates required

So the *next* feature does not re-duplicate, documentation must make Core ownership explicit. In the
M7 step:

- **New `docs/architecture.md` section "Core layer and anti-duplication"** (or a HeimKit-specific
  architecture doc if Trevor wants the two plugins documented separately): state that all shared
  responsibilities live under `Core/`, reproduce the §5 decision table, and state the rule that a
  new feature must consume Core services and may not re-implement one. Reference this spec.
- **Update `AGENTS.md`** "Source changes" guidance: add that feature-owned folders must not contain
  world scanners, prefab caches, native-reflection caches, packet/ownership managers, presence hubs
  or config loaders, and that new shared needs go into `Core/` plus the decision table.
- **Update each `docs/specs/<feature>.md`** "Player-service integration" / implementation section to
  name which Core services the feature consumes (subscription filter, prefab classifications used,
  ownership/packet usage), so the contract is discoverable per feature.
- **Keep** the existing content-placement rules: one guide + one spec per feature, no development
  journals, versions only in `server-setup.md`, testing in chat only.

Do not duplicate version numbers or testing status into any of these. Do not add completion
checklists to committed docs.

## 10. Definition of done (acceptance criteria)

The rebuild is complete when all hold. These are behavioral/structural contracts, verifiable by reading
the code and by in-chat testing with Trevor (no test artifacts committed).

1. **Feature parity.** Every legacy feature exists in the rebuild with identical settings keys, config
   file name and gameplay effects, subject to the approved logging exceptions in section 0.
2. **Single scan.** `ZDOMan.FindSectorObjects` is called in exactly one place (`WorldObjectProvider`).
   No feature constructs `WorldCandidateIndex` or calls `FindSectorObjects`. (Grep proves it.)
3. **Single prefab cache.** `GetComponent`/`GetComponentsInChildren` against prefabs appears only in
   `PrefabRegistry` (and `PrefabCollisionBounds`). No feature keeps a prefab→metadata dictionary.
4. **No hot-path reflection.** No `FieldInfo.GetValue/SetValue` or `MethodInfo.Invoke` in any Harmony
   patch or per-tick loop. All native field access is via `NativeAccess` FieldRefs/delegates.
5. **No `ZDOID.ToString()` keys** in per-tick maps/sets.
6. **No per-tick allocation / no LINQ** in per-tick or per-frame paths; buffers are reused fields.
7. **Staggered, scheduler-driven ticks.** No feature stores its own `_next` throttle float.
8. **Diagnostics cost nothing when off.** Status objects/dictionary copies built only when emitting.
9. **Isolation preserved.** Each module enables/disables independently; one module's failure disables
   only itself; cleanup is complete and reported (per current architecture).
10. **Docs updated** per §9, including the anti-duplication rule and decision table.
11. **No build/version/publish** performed unless Trevor asked.

## 11. Out of scope / non-goals

- No new features or gameplay changes. The approved shared DebugVerbosity setting is the only settings exception.
- No change to the audited game version or server-only compatibility posture.
- Approved logging exceptions permit timestamped topics, rewritten Debug/Error records, the shared DebugVerbosity setting and WeatherGuard ten-minute prevention roll-ups; other Info/Warning wording, severities and existing gates remain unchanged. Player-activity contracts retain their behavior when moved to `Core/`.
- No removal of the legacy `src/HeimKit/` tree or rename of the temporary source/test directories until Trevor authorizes the cutover.
- No deployment, server install, or world changes.

## 12. When you are blocked

If any placement, contract, or behavior is ambiguous after reading this document, the current
implementation, and the feature spec: **stop and ask Trevor in chat.** Do not invent a second copy
of a Core responsibility to make progress, and do not change observable behavior to simplify a port.
