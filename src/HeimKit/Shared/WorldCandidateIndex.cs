using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Shared;

// IDs, never pooled ZDO references, are retained between passes. Each visited
// area is seeded from native records; subsequent local/replicated changes keep
// candidates current. Overflow invalidates coverage and preserves broad fallback.
internal sealed class WorldCandidateIndex : IDisposable
{
    internal const int Capacity = 16384;
    private readonly ZDOMan _world;
    private readonly Func<ZDO, bool> _accept;
    private readonly Action<Vector2s, SimulationDistance, List<ZDO>, List<ZDO>?> _query;
    private readonly bool _observe;
    private readonly HashSet<ZDOID> _changes = new();
    private readonly Dictionary<ZDOID, Vector2s> _candidates = new();
    private readonly Dictionary<Vector2s, HashSet<ZDOID>> _cells = new();
    private readonly HashSet<(int X, int Z, int Radius, bool Classic, bool Distant)> _areas = new();
    private readonly List<ZDOID> _remove = new();
    private readonly List<ZDO> _seed = new(), _distant = new();
    private bool _overflow, _disposed;
    internal long Queries { get; private set; }
    internal WorldCandidateIndex(Func<ZDO, bool> accept,
        Action<Vector2s, SimulationDistance, List<ZDO>, List<ZDO>?>? query = null, bool observe = true)
    {
        _world = ZDOMan.instance; _accept = accept;
        _query = query ?? _world.FindSectorObjects; _observe = observe;
        if (_observe) WorldRecordChanges.Add(this);
        _world.m_onZDODestroyed += Destroyed;
    }
    internal void Enqueue(ZDOID id)
    {
        if (_disposed || !ReferenceEquals(_world, ZDOMan.instance)) return;
        if (_changes.Count < Capacity || _changes.Contains(id)) _changes.Add(id); else _overflow = true;
    }
    private void Destroyed(ZDO record) => Enqueue(record.m_uid);
    internal void Invalidate() => _overflow = true;
    internal void Read(Vector3 center, int radius, List<ZDO> result, bool classic = false, bool includeDistant = true)
    {
        WorldRecordChanges.Dispatch();
        result.Clear();
        if (!ReferenceEquals(_world, ZDOMan.instance)) { _areas.Clear(); _candidates.Clear(); _cells.Clear(); _changes.Clear(); return; }
        if (_overflow) { _areas.Clear(); _candidates.Clear(); _cells.Clear(); _overflow = false; }
        foreach (var id in _changes)
        {
            var record = _world.GetZDO(id);
            if (record != null && record.IsValid() && record.m_uid == id && _accept(record))
            { if (_candidates.Count < Capacity || _candidates.ContainsKey(id)) Track(record); else _overflow = true; }
            else Untrack(id);
        }
        _changes.Clear();
        var zone = ZoneSystem.GetZone(center);
        var area = ((int)zone.x, (int)zone.y, radius, classic, includeDistant);
        if (_overflow || !_areas.Contains(area))
        {
            if (_areas.Count >= 256 || _overflow) { _areas.Clear(); _candidates.Clear(); _cells.Clear(); }
            _seed.Clear(); _distant.Clear(); Queries++;
            _query(zone, new SimulationDistance(radius, 0, classic), _seed, includeDistant ? null : _distant);
            // Do not classify an oversized native snapshot on top of discovery.
            // Consumers retain their normal scope/capacity handling in fallback.
            if (_seed.Count > 65536) { _overflow = true; result.AddRange(_seed); ClearSeed(); return; }
            bool complete = true;
            foreach (var record in _seed)
                if (record.IsValid() && _accept(record))
                {
                    if (_candidates.Count < Capacity || _candidates.ContainsKey(record.m_uid)) Track(record);
                    else complete = false;
                }
            _overflow = !complete;
            if (complete) _areas.Add(area);
            else { result.AddRange(_seed); ClearSeed(); return; }
            ClearSeed();
        }
        _remove.Clear();
        for (int x = zone.x - radius; x <= zone.x + radius; x++)
        for (int z = zone.y - radius; z <= zone.y + radius; z++)
        {
            var sector = new Vector2s(x, z);
            if (!_cells.TryGetValue(sector, out var ids)) continue;
            bool near = classic || ZoneSystem.instance.ZonesWithinRadius(zone, sector, radius);
            bool distant = includeDistant && (classic || ZoneSystem.instance.ZonesWithinRadius(zone, sector, radius, true));
            if (!near && !distant) continue;
            foreach (var id in ids)
            {
                var record = _world.GetZDO(id);
                if (record == null || !record.IsValid() || record.m_uid != id) { _remove.Add(id); continue; }
                if (near || distant && record.Distant) result.Add(record);
            }
        }
        foreach (var id in _remove) Untrack(id);
    }
    private void ClearSeed()
    {
        _seed.Clear(); _distant.Clear();
        if (_seed.Capacity > 65536) _seed.Capacity = 65536;
        if (_distant.Capacity > 65536) _distant.Capacity = 65536;
    }
    private void Track(ZDO record)
    {
        var sector = record.GetSector();
        if (_candidates.TryGetValue(record.m_uid, out var previous))
        { if (previous == sector) return; Untrack(record.m_uid); }
        _candidates[record.m_uid] = sector;
        if (!_cells.TryGetValue(sector, out var ids)) _cells[sector] = ids = new();
        ids.Add(record.m_uid);
    }
    private void Untrack(ZDOID id)
    {
        if (!_candidates.TryGetValue(id, out var sector)) return;
        _candidates.Remove(id);
        if (_cells.TryGetValue(sector, out var ids))
        { ids.Remove(id); if (ids.Count == 0) _cells.Remove(sector); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _world.m_onZDODestroyed -= Destroyed;
        if (_observe) WorldRecordChanges.Remove(this);
        _changes.Clear(); _candidates.Clear(); _cells.Clear(); _areas.Clear(); _seed.Clear();
    }
}
