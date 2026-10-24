using System;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Core.Players;
using UnityEngine;
using HarmonyLib;

namespace HeimKit.Core.World;

internal enum ScanKind { Nearby, NearbyAndDistant, ServerSimulation }

// Native enumeration is cell-granular, so overlapping radii and different native
// simulation geometries share discovery without ever shrinking safety context.
internal sealed class WorldObjectProvider : IDisposable
{
    internal static WorldObjectProvider Current { get; private set; } = null!;
    private readonly IPlayerPresence _presence;
    private readonly Action<Vector2s, SimulationDistance, List<ZDO>, List<ZDO>?>? _nativeQuery;
    private readonly Func<Vector2s, Vector2s, int, bool, bool>? _geometry;
    private readonly bool _observe;
    private const int MaximumCycleCells = 8192;
    private static class LiveInstances
    {
        static LiveInstances() { }
        internal static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Read = NativeAccess.Field<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
    }
    internal static void CheckLiveInstanceCompatibility()
    {
        try { _ = LiveInstances.Read; }
        catch (Exception error) { throw new MissingMemberException("WeatherGuard live scene instance access is unavailable.", error); }
    }
    private int _targetedCount;
    private bool _playersSampled;
    private ZDOMan? _world;
    private long _worldIdentity;
    private WorldCandidateIndex? _index;
    private readonly Dictionary<Vector2s, List<ZDO>> _cycle = new();
    private readonly Stack<List<ZDO>> _buffers = new();
    private readonly AccessOrder<Vector2s> _cycleOrder = new();
    private readonly HashSet<uint> _visitedSectors = new();
    private readonly Dictionary<ZDOID, Vector2s> _rawLocations = new();
    private readonly Dictionary<ZDOID, ZDO> _rawObjects = new();
    private readonly Dictionary<(Vector2s, int, int, bool), BroadView> _views = new();
    private readonly Stack<BroadView> _viewBuffers = new();
    private sealed class BroadView { internal readonly List<ZDO> Near = new(), Far = new(); }
    private readonly Dictionary<string, CandidateSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly List<ZDO> _cell = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly List<PlayerWorldScope> _scopes = new();
    private readonly HashSet<CandidateSubscription> _leases = new();
    private readonly List<CandidateSubscription> _release = new();
    private readonly List<PlayerWorldScope> _releaseScopes = new();
    internal bool Ready { get; private set; }
    internal IReadOnlyList<Vector3> PlayerPositions { get { SamplePlayers(); return Scope.Positions; } }
    internal PlayerWorldScope Scope { get; }
    internal WorldObjectProvider(IPlayerPresence presence,
        Action<Vector2s, SimulationDistance, List<ZDO>, List<ZDO>?>? nativeQuery = null,
        Func<Vector2s, Vector2s, int, bool, bool>? geometry = null, bool observe = true)
    { _presence = presence; _nativeQuery = nativeQuery; _geometry = geometry; _observe = observe; Current = this; Scope = new PlayerWorldScope(presence); }
    internal PlayerWorldScope CreateScope(string owner) { var scope = new PlayerWorldScope(_presence, owner); _scopes.Add(scope); return scope; }
    internal void ReleaseScope(PlayerWorldScope scope) => _scopes.Remove(scope);
    internal CandidateSubscription Subscribe(string owner, Func<ZDO, bool> accept, ScanKind kind = ScanKind.NearbyAndDistant)
    { var subscription = new CandidateSubscription(this, owner, accept, kind); _leases.Add(subscription); _targetedCount++; _index?.Invalidate(); return subscription; }
    internal void Release(CandidateSubscription subscription)
    {
        if (!_leases.Remove(subscription) || subscription.Broad) return;
        if (--_targetedCount == 0) { _index?.Dispose(); _index = null; ResetSelections(); }
    }
    internal void ReleaseOwner(string owner)
    {
        _release.Clear(); foreach (var subscription in _leases) if (subscription.Owner == owner) _release.Add(subscription);
        foreach (var subscription in _release) subscription.Dispose();
        _release.Clear(); _subscriptions.Remove(owner);
        _releaseScopes.Clear(); foreach (var scope in _scopes) if (scope.Owner == owner) _releaseScopes.Add(scope);
        foreach (var scope in _releaseScopes) scope.Dispose(); _releaseScopes.Clear();
        if (_leases.Count == 0 && _scopes.Count == 0) { _index?.Dispose(); _index = null; }
    }
    internal CandidateSubscription For(string owner)
    {
        if (!_subscriptions.TryGetValue(owner, out var result))
        { result = new CandidateSubscription(this, owner, AcceptAll, ScanKind.NearbyAndDistant, broad: true); _subscriptions.Add(owner, result); _leases.Add(result); }
        return result;
    }
    private static bool AcceptAll(ZDO record) => true;
    internal void Tick(float now)
    {
        WorldServices.Refresh(); BeginCycle(WorldServices.World,
            WorldServices.World && WorldServices.Activity ? ZNet.instance.GetWorldUID() : 0);
    }
    internal void SamplePlayers()
    {
        if (_playersSampled) return;
        _playersSampled = true;
        if (!Ready || _presence.HasJoinedPlayers != true || _leases.Count == 0 && _scopes.Count == 0) return;
        Scope.SampleActors();
    }
    internal void BeginCycle(bool ready, long worldIdentity = 0)
    {
        Ready = ready;
        _playersSampled = false;
        foreach (var buffer in _cycle.Values) { buffer.Clear(); _buffers.Push(buffer); }
        _cycle.Clear();
        _cycleOrder.Clear(); _rawLocations.Clear(); _rawObjects.Clear(); ClearViews();
        if (!Ready || !ReferenceEquals(_world, ZDOMan.instance) || _worldIdentity != worldIdentity || _presence.HasJoinedPlayers != true)
        {
            _index?.Dispose(); _index = null; _world = Ready ? ZDOMan.instance : null; _worldIdentity = worldIdentity;
            foreach (var subscription in _leases) subscription.Clear();
            Scope.ClearActors(); foreach (var scope in _scopes) scope.ClearActors();
            if (!Ready || _presence.HasJoinedPlayers != true) return;
        }
    }
    private void NativeCell(Vector2s sector, SimulationDistance ignored, List<ZDO> records, List<ZDO>? distant)
    {
        if (_cycle.TryGetValue(sector, out var saved)) { _cycleOrder.Touch(sector); records.AddRange(saved); return; }
        // Evict one cache entry rather than disabling a module or wiping coverage.
        if (_cycle.Count >= CellBudget)
        {
            var expired = _cycleOrder.RemoveOldest(); var old = _cycle[expired];
            foreach (var record in old) if (_rawLocations.TryGetValue(record.m_uid, out var location) && location == expired) { _rawLocations.Remove(record.m_uid); _rawObjects.Remove(record.m_uid); }
            _cycle.Remove(expired); old.Clear(); _buffers.Push(old);
        }
        // Radius zero returns the complete cell for indexed/shared discovery.
        NativeScan(sector, new SimulationDistance(0, 0, true), records, distant);
        saved = _buffers.Count > 0 ? _buffers.Pop() : new List<ZDO>();
        saved.AddRange(records); _cycle.Add(sector, saved); _cycleOrder.Add(sector);
        foreach (var record in saved) { _rawLocations[record.m_uid] = sector; _rawObjects[record.m_uid] = record; }
    }
    private void NativeScan(Vector2s sector, SimulationDistance distance, List<ZDO> records, List<ZDO>? distant)
    {
        // The sole native sector enumeration in HeimKit. Broad-only setups
        // use the original native scan without an index, hooks or frame cache.
        if (_nativeQuery != null) _nativeQuery(sector, distance, records, distant);
        else _world!.FindSectorObjects(sector, distance, records, distant);
    }
    private int CellBudget => Math.Min(32768, Math.Max(MaximumCycleCells, (_presence.JoinedCount ?? 1) * 512));
    internal void Enqueue(ZDOID id) => _index?.Enqueue(id);
    private WorldCandidateIndex Index => _index ??= new WorldCandidateIndex(Classify, NativeCell, _observe,
        retainCandidates: false, areaCapacity: CellBudget, evictCell: ForgetCell, changed: Changed, reset: ResetSelections);
    // Membership belongs to targeted selections, never an all-object ID index.
    private bool Classify(ZDO record) { foreach (var subscription in _leases) subscription.Classify(record); return false; }
    private void ForgetCell(Vector2s cell) { foreach (var subscription in _leases) subscription.ForgetCell(cell); }
    private void ResetSelections()
    {
        foreach (var subscription in _leases) subscription.Clear();
        foreach (var buffer in _cycle.Values) { buffer.Clear(); _buffers.Push(buffer); }
        _cycle.Clear(); _cycleOrder.Clear(); _rawLocations.Clear(); _rawObjects.Clear(); ClearViews();
    }
    private void Changed(ZDOID id)
    {
        var record = _world!.GetZDO(id);
        bool valid = record != null && record.IsValid() && record.m_uid == id;
        // Valid records are reclassified in place; removing them here would
        // defeat same-cell membership reuse for single-object cells.
        if (!valid) foreach (var subscription in _leases) subscription.Remove(id);
        if (_rawLocations.TryGetValue(id, out var previous) && _cycle.TryGetValue(previous, out var old))
        {
            if (valid && record!.GetSector() == previous && _rawObjects.TryGetValue(id, out var cached) && ReferenceEquals(cached, record)) { ClearViews(); return; }
            // Keep the saved reference under its original identity: a pooled ZDO
            // can already have a different UID when its destruction hint drains.
            if (_rawObjects.TryGetValue(id, out var removed))
                for (int i = 0; i < old.Count; i++) if (ReferenceEquals(old[i], removed)) { old.RemoveAt(i); break; }
        }
        _rawLocations.Remove(id); _rawObjects.Remove(id);
        if (valid && _cycle.TryGetValue(record!.GetSector(), out var current))
        { current.Add(record!); _rawLocations[id] = record!.GetSector(); _rawObjects[id] = record; }
        ClearViews();
    }
    private void ClearViews() { foreach (var view in _views.Values) { view.Near.Clear(); view.Far.Clear(); _viewBuffers.Push(view); } _views.Clear(); }
    internal void FilterCell(Vector2s cell, Func<ZDO, bool> accept, bool near, List<ZDO> result)
    {
        _cell.Clear(); NativeCell(cell, default, _cell, null);
        foreach (var record in _cell) if (record.IsValid() && record.GetSector() == cell && (near || record.Distant) && accept(record)) result.Add(record);
    }
    private bool Within(Vector2s origin, Vector2s cell, int radius, bool distant) =>
        _geometry != null ? _geometry(origin, cell, radius, distant) : ZoneSystem.instance.ZonesWithinRadius(origin, cell, radius, distant);
    internal void Query(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? distant = null)
        => Compose(null, sector, distance, near, distant);
    internal void QueryCandidates(CandidateSubscription selection, Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? distant)
        => Compose(selection, sector, distance, near, distant);
    private void Compose(CandidateSubscription? selection, Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? distant)
    {
        if (!Ready || _presence.HasJoinedPlayers != true) return;
        _world ??= ZDOMan.instance;
        if (selection == null && _targetedCount == 0)
        {
            NativeScan(sector, distance, near, distant);
            return;
        }
        Index.AreaCapacity = CellBudget;
        Index.RefreshChanges();
        if (selection == null)
        {
            ComposeBroad(sector, distance, near, distant);
            return;
        }
        // Targeted indexes retain their established row ordering.
        int total = distance.TotalSimulationDistance;
        for (int x = sector.x - total; x <= sector.x + total; x++)
        for (int z = sector.y - total; z <= sector.y + total; z++)
        {
            var cell = new Vector2s(x, z);
            int ring = Math.Max(Math.Abs(x - sector.x), Math.Abs(z - sector.y));
            bool isNear = ring <= distance.NearSimulationDistance && (distance.IsClassic || Within(sector, cell, distance.NearSimulationDistance, false));
            bool isFar = ring <= total && (!distance.IsClassic || ring > distance.NearSimulationDistance) &&
                (distance.IsClassic || Within(sector, cell, total, true));
            if (!isNear && !isFar) continue;
            _cell.Clear(); Index.Read(ZoneSystem.GetZonePos(cell), 0, _cell, true);
            if (isNear) selection.AppendCell(cell, true, near);
            else if (isFar && selection.Kind != ScanKind.Nearby) selection.AppendCell(cell, false, distant ?? near);
        }
    }
    private void ComposeBroad(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? distant)
    {
        var key = (sector, distance.NearSimulationDistance, distance.TotalSimulationDistance, distance.IsClassic);
        if (_views.TryGetValue(key, out var cached)) { near.AddRange(cached.Near); (distant ?? near).AddRange(cached.Far); return; }
        if (_views.Count >= CellBudget) ClearViews();
        var view = _viewBuffers.Count > 0 ? _viewBuffers.Pop() : new BroadView();
        _visitedSectors.Clear();
        AppendBroadCell(sector, view, true);
        for (int radius = 1; radius <= distance.NearSimulationDistance; radius++)
            AppendBroadRing(sector, radius, distance.NearSimulationDistance, distance.IsClassic, view, true);
        int start = distance.IsClassic ? distance.NearSimulationDistance + 1 : 1;
        for (int radius = start; radius <= distance.TotalSimulationDistance; radius++)
            AppendBroadRing(sector, radius, distance.TotalSimulationDistance, distance.IsClassic, view, false);
        _views.Add(key, view); near.AddRange(view.Near); (distant ?? near).AddRange(view.Far);
    }
    private void AppendBroadRing(Vector2s center, int radius, int limit, bool classic, BroadView view, bool near)
    {
        // Match native FindSectorObjects: alternating bottom/top cells first,
        // then alternating left/right cells, from the centre outward.
        for (int x = center.x - radius; x <= center.x + radius; x++)
        {
            var bottom = new Vector2s(x, center.y - radius); var top = new Vector2s(x, center.y + radius);
            bool allowed = classic || Within(center, bottom, limit, !near);
            if (allowed) AppendBroadCell(bottom, view, near);
            // Native distant scanning uses the bottom-cell test for both sides.
            if (near ? classic || Within(center, top, limit, false) : allowed) AppendBroadCell(top, view, near);
        }
        for (int z = center.y - radius + 1; z <= center.y + radius - 1; z++)
        {
            var left = new Vector2s(center.x - radius, z); var right = new Vector2s(center.x + radius, z);
            bool allowed = classic || Within(center, left, limit, !near);
            if (allowed) AppendBroadCell(left, view, near);
            if (near ? classic || Within(center, right, limit, false) : allowed) AppendBroadCell(right, view, near);
        }
    }
    private void AppendBroadCell(Vector2s cell, BroadView view, bool near)
    {
        // Native discovery deduplicates native sector indices, including the
        // shared out-of-world sector. Use uint keys to avoid struct boxing.
        if (!_visitedSectors.Add(ZoneSystem.SectorToIndex(cell).Sector)) return;
        _cell.Clear(); NativeCell(cell, default, _cell, null);
        var game = Game.instance;
        var portalHashes = game == null ? null : game.PortalPrefabHash;
        foreach (var record in _cell)
        {
            if (!record.IsValid()) continue;
            if (near) view.Near.Add(record);
            else if (record.Distant && (portalHashes == null || !portalHashes.Contains(record.GetPrefab()))) view.Far.Add(record);
        }
    }
    internal void IncludeLiveInstances(List<ZDO> near, List<ZDO> distant, IReadOnlyList<Vector3> positions)
    {
        _seen.Clear(); foreach (var z in near) _seen.Add(z.m_uid); foreach (var z in distant) _seen.Add(z.m_uid);
        foreach (var pair in LiveInstances.Read(ZNetScene.instance))
        {
            var record = pair.Key;
            if (!record.IsValid() || _seen.Contains(record.m_uid)) continue;
            foreach (var position in positions)
                if (ZNetScene.InActiveArea(record.GetPosition(), position)) { _seen.Add(record.m_uid); near.Add(record); break; }
        }
    }
    public void Dispose()
    {
        _index?.Dispose(); _index = null;
        foreach (var subscription in _subscriptions.Values) subscription.Dispose();
        _subscriptions.Clear(); _leases.Clear(); _targetedCount = 0; _cycle.Clear(); _buffers.Clear(); _cycleOrder.Clear(); _rawLocations.Clear(); _rawObjects.Clear(); _visitedSectors.Clear(); ClearViews(); _viewBuffers.Clear(); _scopes.Clear(); Scope.ClearActors();
        if (ReferenceEquals(Current, this)) Current = null!;
    }
}
