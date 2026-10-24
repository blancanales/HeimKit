using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Core.World;

internal sealed class CandidateSubscription : IDisposable
{
    private readonly WorldObjectProvider _provider;
    private readonly Func<ZDO, bool> _accept;
    private readonly List<ZDO> _records = new();
    private readonly Dictionary<Vector2s, HashSet<ZDOID>> _cells = new();
    private readonly Dictionary<ZDOID, Vector2s> _locations = new();
    private readonly List<ZDOID> _expired = new();
    private bool _fallback;
    private Exception? _failure;
    private readonly List<ZDO> _ignored = new();
    private bool _disposed;
    private readonly List<ZDO> _nearby = new();
    private readonly CandidateView _nearbyView;
    // Borrowed until the next query on this subscription. Callers cannot mutate it.
    internal CandidateView? Nearby(Vector3 point) { _nearby.Clear(); Query(ZoneSystem.GetZone(point), new SimulationDistance(1, 0), _nearby); return _nearby.Count <= 8192 ? _nearbyView : null; }
    internal string Owner { get; }
    internal ScanKind Kind { get; }
    internal bool Broad { get; }
    internal IReadOnlyList<ZDO> Records => _records;
    internal CandidateSubscription(WorldObjectProvider provider, string owner, Func<ZDO, bool> accept, ScanKind kind, bool broad = false)
    { _provider = provider; Owner = owner; _accept = accept; Kind = kind; Broad = broad; _nearbyView = new CandidateView(_nearby); }
    internal void Classify(ZDO record)
    {
        if (Broad || _fallback || _failure != null) return;
        try { if (!_accept(record)) { Remove(record.m_uid); return; } }
        catch (Exception error) { Remove(record.m_uid); _failure = error; return; }
        var cell = record.GetSector();
        if (_locations.TryGetValue(record.m_uid, out var previous) && previous == cell) return;
        Remove(record.m_uid);
        // A very large selection uses shared broad views instead of repeatedly
        // invalidating discovery for every other module.
        if (_locations.Count >= WorldCandidateIndex.Capacity) { Clear(); _fallback = true; return; }
        if (!_cells.TryGetValue(cell, out var ids)) _cells.Add(cell, ids = new());
        ids.Add(record.m_uid); _locations.Add(record.m_uid, cell);
    }
    internal void Remove(ZDOID id)
    {
        if (!_locations.TryGetValue(id, out var cell)) return;
        _locations.Remove(id);
        if (_cells.TryGetValue(cell, out var ids)) { ids.Remove(id); if (ids.Count == 0) _cells.Remove(cell); }
    }
    internal void ForgetCell(Vector2s cell)
    {
        if (!_cells.TryGetValue(cell, out var ids)) return;
        foreach (var id in ids) _locations.Remove(id);
        _cells.Remove(cell);
    }
    internal void Clear() { _cells.Clear(); _locations.Clear(); _fallback = false; }
    internal void AppendCell(Vector2s cell, bool near, List<ZDO> result)
    {
        if (_fallback) { _provider.FilterCell(cell, _accept, near, result); return; }
        if (!_cells.TryGetValue(cell, out var ids)) return;
        _expired.Clear();
        foreach (var id in ids)
        {
            var record = ZDOMan.instance.GetZDO(id);
            if (record == null || !record.IsValid() || record.m_uid != id || record.GetSector() != cell) { _expired.Add(id); continue; }
            if (near || record.Distant) result.Add(record);
        }
        foreach (var id in _expired) Remove(id);
    }
    internal void Query(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? distant = null)
    {
        if (_disposed) throw new ObjectDisposedException(Owner);
        if (_failure != null) throw new InvalidOperationException(Owner + " candidate classification failed.", _failure);
        if (Broad && Kind == ScanKind.Nearby) _ignored.Clear();
        _records.Clear();
        int beforeNear = near.Count, beforeFar = distant?.Count ?? 0;
        if (Broad) _provider.Query(sector, distance, near, Kind == ScanKind.Nearby ? _ignored : distant);
        else _provider.QueryCandidates(this, sector, distance, near, distant);
        if (_failure != null) throw new InvalidOperationException(Owner + " candidate classification failed.", _failure);
        for (int i = beforeNear; i < near.Count; i++) _records.Add(near[i]);
        if (distant != null && !ReferenceEquals(distant, near)) for (int i = beforeFar; i < distant.Count; i++) _records.Add(distant[i]);
    }
    internal void Read(Vector3 point, int radius, List<ZDO> records, bool classic = false, bool includeDistant = true)
    {
        records.Clear(); _ignored.Clear();
        Query(ZoneSystem.GetZone(point), new SimulationDistance(radius, 0, classic), records, includeDistant ? null : _ignored);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); _records.Clear(); _nearby.Clear(); _ignored.Clear(); _provider.Release(this); }
}

// Read-only borrowed view with a struct enumerator for immediate hot-path use.
internal sealed class CandidateView : IReadOnlyList<ZDO>
{
    private readonly List<ZDO> _records;
    internal CandidateView(List<ZDO> records) => _records = records;
    public int Count => _records.Count;
    public ZDO this[int index] => _records[index];
    public List<ZDO>.Enumerator GetEnumerator() => _records.GetEnumerator();
    IEnumerator<ZDO> IEnumerable<ZDO>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
