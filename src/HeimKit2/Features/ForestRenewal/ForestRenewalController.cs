using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Logging;
using HeimKit.Features.ForestRenewal.Models;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.ForestRenewal;
internal sealed class ForestRenewalController : IDisposable
{
    private readonly ForestRenewal _module;
    private readonly ForestRenewalSettings _settings;
    private readonly ManualLogSource _log;
    private readonly ForestRenewalDiagnostics _diagnostics;
    private readonly PlayerWorldScope _scope;
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly ForestRenewalStore _store;
    private readonly TerrainContext _terrain;
    private readonly ForestRenewalStructures _structures = new();
    private readonly ForestRenewalReplacement _replacement;
    private readonly PrefabCatalog<GameObject> _saplings = PrefabRegistry.Current.Catalog<GameObject>("ForestRenewalController._saplings");
    private readonly PrefabCatalog<int> _mappings = PrefabRegistry.Current.Catalog<int>("ForestRenewalController._mappings");
    private readonly HashSet<RenewalRecord> _pending = new();
    private readonly List<RenewalRecord> _due = new();
    private readonly Dictionary<(int X, int Z), HashSet<RenewalRecord>> _cooldowns = new();
    private readonly PrefabCatalog<(GameObject Prefab, Plant? Plant, bool Tree)> _vegetation = PrefabRegistry.Current.Catalog<(GameObject Prefab, Plant? Plant, bool Tree)>("ForestRenewalController._vegetation");
    private float _scopeAt = -1000;
    private long _placed;
    private int _pendingCursor;
    private int _pendingCount;
    private int _destroyedIntake;
    private bool _disposed, _capacityWarning, _dirty;
    private long _revision, _writingRevision, _durableRevision;
    private Task<double>? _write;
    private RenewalPlacement? _placement;
    public ForestRenewalController(ForestRenewal module, ForestRenewalSettings settings, ManualLogSource log, PlayerServices players, string directory)
    {
        _module = module; _settings = settings; _log = log; _diagnostics = new(log, settings.DebugLogging); _scope = WorldObjectProvider.Current.CreateScope("ForestRenewal");
        foreach (string name in new[] { "Beech_Sapling", "Birch_Sapling", "Oak_Sapling", "FirTree_Sapling", "PineTree_Sapling", "FirTree_big_Sapling" })
        {
            var prefab = PrefabRegistry.Current.Prefab(name); var plant = PrefabRegistry.Component<Plant>(prefab);
            if (plant == null || PrefabRegistry.Component<ZNetView>(prefab)?.m_persistent != true) continue;
            var stumps = plant.m_grownPrefabs.Select(p => PrefabRegistry.Component<TreeBase>(p)?.m_stubPrefab).ToArray();
            if (stumps.Length == 0 || stumps.Any(s => s == null || PrefabRegistry.Component<Destructible>(s) == null)) continue;
            int hash = name.GetStableHashCode(); _saplings.Add(hash, prefab!);
            foreach (var stump in stumps.Distinct())
            {
                int source = stump!.name.GetStableHashCode();
                if (_mappings.TryGetValue(source, out int existing) && existing != hash) throw new InvalidOperationException("Ambiguous native tree-to-sapling mapping.");
                _mappings[source] = hash;
            }
        }
        if (_mappings.Count == 0) throw new InvalidOperationException("No supported native saplings are available.");
        _store = new(directory, ZNet.instance.GetWorldUID());
        _pendingCount = _store.Records.Values.Count(Pending);
        foreach (var record in _store.Records.Values)
        {
            if (Pending(record)) _pending.Add(record);
            if (record.State == 2) IndexCooldown(record, true);
        }
        _terrain = new();
        try { _replacement = new(module.Fail, log, players.Presence); } catch { _terrain.Dispose(); throw; }
        _world.m_onZDODestroyed += Destroyed;
        try { _scope.Index(z => _mappings.ContainsKey(z.GetPrefab())); } catch { Dispose(); throw; }
    }
    private void Destroyed(ZDO source)
    {
        if (_disposed || !_module.IsActive || !ReferenceEquals(_world, ZDOMan.instance)) return;
        try
        {
            if (!_mappings.TryGetValue(source.GetPrefab(), out int sapling)) return;
            if (_store.Records.TryGetValue(source.m_uid, out var existing))
            {
                // A confirmed manual removal during the durable-write wait preserves the
                // same consumed placement; absence without this event still cannot do so.
                if (_placement != null && ReferenceEquals(_placement.Decision, existing) && ReferenceEquals(_placement.Stump, source))
                { _placement.Stump = null; _placement.PendingState = 0; }
                // Both triggers share the original roll/timer. A real destruction event is
                // required before a standing-stump entry can renew an absent source.
                if (existing.State == 3) { existing.State = 0; MarkDirty(); }
                return;
            }
            if (Time.unscaledTime - _scopeAt > 2 * _settings.IntervalSeconds.Value) return;
            var position = source.GetPosition();
            if (!PlayerWorldScope.Finite(position) || Character.InInterior(position) || !NearObservedPlayer(position)) return;
            if (_destroyedIntake < 32 && Register(source, sapling, false)) { _destroyedIntake++; MarkDirty(); }
        }
        catch (Exception error) { _module.Fail(error); }
    }
    private bool NearObservedPlayer(Vector3 position)
    {
        float squared = _settings.ScanRangeMeters.Value * _settings.ScanRangeMeters.Value;
        foreach (var player in _scope.Positions) if ((player - position).sqrMagnitude <= squared) return true;
        return false;
    }
    public void Tick(float now)
    {
        if (!ModuleScheduler.Current.Due(this, "processing", now, _settings.IntervalSeconds.Value)) return;
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _scope.Records.Count) : null;
        PollSave(now);
        _destroyedIntake = 0;
        long scopeStarted = _diagnostics.BeginStage();
        bool scopeReady;
        try { scopeReady = _scope.Refresh(_settings.ScanRangeMeters.Value); }
        finally { _diagnostics.EndStage(now, "scope_refresh", scopeStarted); }
        if (!scopeReady) { _scopeAt = -1000; _terrain.Clear(); Flush(); return; }
        _scopeAt = now; long ticks = ZNet.instance.GetTime().Ticks; int examined = 0, placed = 0;
        int observed = 0;
        foreach (var stump in _scope.Records)
        {
            if (!stump.IsValid() || !_mappings.TryGetValue(stump.GetPrefab(), out int sapling) || _store.Records.ContainsKey(stump.m_uid) ||
                !_scope.InRange(stump.GetPosition(), _settings.ScanRangeMeters.Value) || Character.InInterior(stump.GetPosition())) continue;
            if (!Register(stump, sapling, true)) break;
            if (++observed >= 32) break;
        }
        if (observed > 0) MarkDirty();
        bool hadPlacement = _placement != null;
        if (hadPlacement) placed += CompletePlacement(now, ticks);
        var due = _due; due.Clear();
        if (!hadPlacement) foreach (var pending in _pending) if (pending.DueTicks <= ticks) due.Add(pending);
        int start = due.Count == 0 ? 0 : _pendingCursor % due.Count;
        for (int visited = 0; !hadPlacement && visited < due.Count; visited++)
        {
            int index = (start + visited) % due.Count;
            var record = due[index];
            _pendingCursor = (index + 1) % due.Count;
            if (!_scope.InRange(record.Position, _settings.ScanRangeMeters.Value)) continue;
            var source = _world.GetZDO(record.Source);
            if (record.State == 3 && source == null) continue; // Absence is not a destruction event.
            if (source != null && (!source.IsValid() || !_mappings.TryGetValue(source.GetPrefab(), out int mapped) || mapped != record.Sapling || (source.GetPosition() - record.Position).sqrMagnitude > 0.01f)) continue;
            if (++examined > 1) { _pendingCursor = index; break; }
            if (!_saplings.TryGetValue(record.Sapling, out var prefab)) { Consume(record, ticks, 1); continue; }
            if (!StructureKnown(record, now, out bool nearStructure)) continue;
            if (nearStructure) { _diagnostics.Decision(now, record.Source, "structure_buffer"); Consume(record, ticks, 1); continue; }
            if (!PrepareTerrain(record.Position, now)) continue;
            if (!ContextKnown(record, prefab)) continue;
            if (Excluded(record, ticks, prefab)) { _diagnostics.Decision(now, record.Source, "plant_tree_terrain_or_cooldown"); Consume(record, ticks, 1); continue; }
            var point = record.Position;
            if (!ZoneSystem.instance.GetGroundHeight(point, out float height) || !PlayerWorldScope.Finite(height)) continue;
            point.y = height;
            float liquid = Floating.GetLiquidLevel(point, 0, LiquidType.All);
            if (liquid > point.y) { Consume(record, ticks, 1); continue; }
            byte pendingState = record.State; long previousDecisionTicks = record.DecisionTicks;
            Consume(record, ticks, 2);
            _placement = new RenewalPlacement { Decision = record, Stump = source, Prefab = prefab, Position = point,
                RequiredRevision = _revision, Created = now, PendingState = pendingState, PreviousDecisionTicks = previousDecisionTicks };
            break;
        }
        Flush();
        if (placed > 0) DiagnosticLog.Write(_log, "ForestRenewal", placed == 1 ? "Placed one tree sapling." : $"Placed {placed} tree saplings.");
        if (_capacityWarning && _store.Records.Count < ForestRenewalStore.Capacity && PendingWork < _settings.MaxPending.Value)
        { _capacityWarning = false; DiagnosticLog.Write(_log, "ForestRenewal", "Pending renewal capacity is available again."); }
        _diagnostics.Result(now, placed, _placed, PendingWork);
    }
    private static bool Pending(RenewalRecord record) => record.State == 0 || record.State == 3;
    private int PendingWork => _pendingCount + (_placement != null ? 1 : 0);
    private bool Register(ZDO source, int sapling, bool standing)
    {
        if (_store.Records.Count >= ForestRenewalStore.Capacity || PendingWork >= _settings.MaxPending.Value)
        {
            if (!_capacityWarning) { _capacityWarning = true; DiagnosticLog.Write(_log, "ForestRenewal", "Renewal ledger capacity reached; new stumps will be skipped.", level: LogLevel.Warning); }
            return false;
        }
        long ticks = ZNet.instance.GetTime().Ticks;
        var record = new RenewalRecord { Source = source.m_uid, Position = source.GetPosition(), Sapling = sapling,
            DueTicks = ticks + TimeSpan.FromSeconds(_settings.DelaySeconds.Value).Ticks, DecisionTicks = ticks,
            State = (byte)(UnityEngine.Random.Range(0, 100) < _settings.ChancePercent.Value ? (standing ? 3 : 0) : 1) };
        _store.Records.Add(record.Source, record); if (Pending(record)) { _pendingCount++; _pending.Add(record); } return true;
    }
    private void MarkDirty() { _dirty = true; _revision++; }
    private void Flush()
    {
        if (!_dirty || _write != null) return;
        _writingRevision = _revision;
        long started = _diagnostics.BeginStage();
        try { _write = _store.SaveAsync(); _dirty = false; }
        finally { _diagnostics.EndStage(Time.unscaledTime, "ledger_snapshot", started); }
    }
    private void PollSave(float now)
    {
        if (_write == null || !_write.IsCompleted) return;
        double duration = _write.GetAwaiter().GetResult();
        _write = null; _durableRevision = _writingRevision;
        _diagnostics.LedgerWrite(now, duration);
    }
    private void Consume(RenewalRecord record, long ticks, byte state)
    {
        bool pending = Pending(record);
        if (record.State == 2) IndexCooldown(record, false);
        record.State = state;
        if (record.State == 2) IndexCooldown(record, true);
        if (Pending(record)) _pending.Add(record); else _pending.Remove(record);
        if (pending && !Pending(record)) _pendingCount--;
        else if (!pending && Pending(record)) _pendingCount++;
        record.DecisionTicks = ticks; MarkDirty();
    }
    private int CompletePlacement(float now, long ticks)
    {
        var placement = _placement!;
        var record = placement.Decision;
        if (now - placement.Created > 60)
        { RetryPlacement(now, "placement_context_retry"); return 0; }
        if (!ForestRenewalCommitRules.Ready(placement, _durableRevision) || !_scope.InRange(record.Position, _settings.ScanRangeMeters.Value)) return 0;
        var source = _world.GetZDO(record.Source);
        if (!ReferenceEquals(source, placement.Stump) || source != null && (!source.IsValid() || source.m_uid != record.Source ||
            !_mappings.TryGetValue(source.GetPrefab(), out int mapped) || mapped != record.Sapling || (source.GetPosition() - record.Position).sqrMagnitude > .01f))
        { RetryPlacement(now, "placement_source_retry"); return 0; }
        if (!StructureKnown(record, now, out bool blocked)) return 0;
        if (blocked) { _diagnostics.Decision(now, record.Source, "placement_structure_buffer"); Consume(record, ticks, 1); _placement = null; return 0; }
        if (!PrepareTerrain(record.Position, now) || !ContextKnown(record, placement.Prefab)) return 0;
        if (Excluded(record, ticks, placement.Prefab))
        { _diagnostics.Decision(now, record.Source, "placement_exclusion_changed"); Consume(record, ticks, 1); _placement = null; return 0; }
        var point = placement.Position;
        if (!ZoneSystem.instance.GetGroundHeight(point, out float height) || !PlayerWorldScope.Finite(height)) return 0;
        point.y = height;
        if (Floating.GetLiquidLevel(point, 0, LiquidType.All) > point.y)
        { _diagnostics.Decision(now, record.Source, "placement_submerged"); Consume(record, ticks, 1); _placement = null; return 0; }
        // This exact decision generation has completed its durable write successfully.
        long started = _diagnostics.BeginStage();
        placement.MutationStarted = true;
        try { _replacement.Place(source, placement.Prefab, point, ticks); }
        finally { _diagnostics.EndStage(now, "world_mutation", started); }
        _placement = null; _placed++;
        _diagnostics.Decision(now, record.Source, source == null ? "cleared_position_planted" : "standing_stump_replaced");
        return 1;
    }
    private void RetryPlacement(float now, string reason)
    {
        var placement = _placement!;
        IndexCooldown(placement.Decision, false);
        ForestRenewalCommitRules.RestorePending(placement);
        _pending.Add(placement.Decision);
        _pendingCount++;
        MarkDirty(); _placement = null;
        _diagnostics.Decision(now, placement.Decision.Source, reason);
    }
    private bool PrepareTerrain(Vector3 position, float now)
    {
        long started = _diagnostics.BeginStage();
        try { return _scope.ContextReady() && _terrain.Prepare(position, _scope.Context, now, _settings.ExclusionRadiusMeters.Value + 2); }
        finally { _diagnostics.EndStage(now, "terrain_context", started); }
    }
    private bool StructureKnown(RenewalRecord record, float now, out bool blocked)
    {
        long started = _diagnostics.BeginStage();
        try
        {
            if (!_scope.ContextReady()) { blocked = false; return false; }
            bool known = _structures.TryExcluded(_scope.Context, record.Position, _settings.StructureExclusionMeters.Value, out blocked, out var blocker, out string? reason);
            if (!known) _diagnostics.Decision(now, record.Source, "structure_context_unknown", blocker, reason);
            return known;
        }
        finally { _diagnostics.EndStage(now, "structure_exclusion", started); }
    }
    private bool ContextKnown(RenewalRecord record, GameObject sapling)
    {
        float growRadius = PrefabRegistry.Component<Plant>(sapling)!.m_growRadius;
        float radius = _settings.ExclusionRadiusMeters.Value;
        for (float x = -radius; x <= radius; x++)
            for (float z = -radius; z <= radius; z++)
            { var map = Heightmap.FindHeightmap(record.Position + new Vector3(x, 0, z)); if (map == null || map.HaveQueuedRebuild()) return false; }
        foreach (var zdo in _scope.Context)
        {
            if (!zdo.IsValid()) continue;
            var definition = Vegetation(zdo.GetPrefab());
            if (Utils.DistanceXZ(zdo.GetPosition(), record.Position) <= Math.Max(growRadius, Math.Max(_settings.PlantExclusionMeters.Value, _settings.StructureExclusionMeters.Value)) && definition.Prefab == null) return false;
            var plant = definition.Plant;
            if (plant != null && (!PlayerWorldScope.Finite(plant.m_growRadius) || !PlantScale(zdo, plant.gameObject, out _))) return false;
        }
        return true;
    }
    private bool Excluded(RenewalRecord record, long ticks, GameObject sapling)
    {
        if (CoolingDown(record, ticks, TimeSpan.FromHours(_settings.LocationCooldownHours.Value).Ticks)) return true;
        float growRadius = PrefabRegistry.Component<Plant>(sapling)!.m_growRadius;
        foreach (var zdo in _scope.Context)
        {
            if (!zdo.IsValid()) continue;
            if (zdo.m_uid == record.Source) continue;
            var definition = Vegetation(zdo.GetPrefab()); var prefab = definition.Prefab;
            if (prefab == null) continue;
            float distance = Utils.DistanceXZ(zdo.GetPosition(), record.Position);
            var plant = definition.Plant;
            if (plant != null)
            {
                // Preserve all planted vegetation, even unhealthy/ownerless plants. Both
                // native growth radii remain minimums beneath the configured plant buffer.
                if (!PlantScale(zdo, prefab, out float scale)) return true;
                float clearance = Math.Max(_settings.PlantExclusionMeters.Value, Math.Max(growRadius, plant.m_growRadius * scale));
                if (distance <= clearance) return true;
            }
            if (distance <= 2 && (definition.Tree || _mappings.ContainsKey(zdo.GetPrefab()))) return true;
        }
        if (!_settings.ExcludeClearedGround.Value) return false;
        float radius = _settings.ExclusionRadiusMeters.Value;
        for (float x = -radius; x <= radius; x++)
            for (float z = -radius; z <= radius; z++)
            {
                var point = record.Position + new Vector3(x, 0, z); var map = Heightmap.FindHeightmap(point);
                // Unknown context must have been deferred by terrain preparation.
                if (map == null || map.HaveQueuedRebuild()) return true;
                if (map.IsCleared(point) || map.IsCultivated(point)) return true;
            }
        return false;
    }
    private bool CoolingDown(RenewalRecord record, long ticks, long cooldown)
    {
        var cell = CooldownCell(record.Position);
        for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                if (_cooldowns.TryGetValue((cell.X + x, cell.Z + z), out var nearby))
                    foreach (var other in nearby)
                        if (other.Source != record.Source && ticks - other.DecisionTicks < cooldown)
                        {
                            var delta = other.Position - record.Position;
                            if (delta.x * delta.x + delta.z * delta.z < 25) return true;
                        }
        return false;
    }
    private (GameObject? Prefab, Plant? Plant, bool Tree) Vegetation(int hash)
    {
        if (_vegetation.TryGetValue(hash, out var definition)) return definition;
        var prefab = PrefabRegistry.Current.Prefab(hash);
        if (prefab == null) return (null, null, false);
        definition = (prefab, PrefabRegistry.Component<Plant>(prefab), PrefabRegistry.Component<TreeBase>(prefab) != null);
        if (_vegetation.Count < 4096) _vegetation[hash] = definition;
        return definition;
    }
    private static (int X, int Z) CooldownCell(Vector3 point) => ((int)Math.Floor(point.x / 5), (int)Math.Floor(point.z / 5));
    private void IndexCooldown(RenewalRecord record, bool add)
    {
        var cell = CooldownCell(record.Position);
        if (add)
        {
            if (!_cooldowns.TryGetValue(cell, out var records)) _cooldowns[cell] = records = new();
            records.Add(record);
        }
        else if (_cooldowns.TryGetValue(cell, out var records))
        { records.Remove(record); if (records.Count == 0) _cooldowns.Remove(cell); }
    }
    private static bool PlantScale(ZDO record, GameObject prefab, out float maximum)
    {
        var scale = record.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (scale == Vector3.zero)
        {
            float scalar = record.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
            scale = float.IsNaN(scalar) ? prefab.transform.localScale : Vector3.one * scalar;
        }
        maximum = Math.Max(scale.x, Math.Max(scale.y, scale.z));
        return PlayerWorldScope.Finite(scale) && scale.x > 0 && scale.y > 0 && scale.z > 0 && maximum <= 16;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _world.m_onZDODestroyed -= Destroyed;
        try
        {
            if (_placement != null && !_placement.MutationStarted) RetryPlacement(Time.unscaledTime, "placement_paused_before_mutation");
            // Only lifecycle cleanup may wait for disk; no background task can outlive this controller.
            if (_write != null) { _write.GetAwaiter().GetResult(); _write = null; }
            if (_dirty) { _store.Save(); _dirty = false; }
            _placement = null;
        }
        finally { try { _replacement.Dispose(); } finally { try { _scope.Dispose(); } finally { _terrain.Dispose(); } } }
    }
}
