using System;
using System.Collections.Generic;
using HeimKit.Core.World;
namespace HeimKit.Features.StationSupply;

// Per-placed-station visit order; unrelated stations cannot change its next intake.
internal sealed class StationSupplyIntakeOrder
{
    internal const int Capacity = 1024;
    private readonly Dictionary<ZDOID, (ZDO Record, int Prefab, int Intakes, int Next)> _entries = new();
    private readonly AccessOrder<ZDOID> _order = new();
    internal int Count => _entries.Count;
    internal int Start(ZDO station, int intakes)
    {
        if (intakes < 1) throw new ArgumentOutOfRangeException(nameof(intakes));
        int start = 0, prefab = station.GetPrefab();
        if (intakes == 1)
        {
            if (_entries.ContainsKey(station.m_uid))
            { _entries[station.m_uid] = (station, prefab, 1, 0); _order.Touch(station.m_uid); }
            return 0;
        }
        if (_entries.TryGetValue(station.m_uid, out var entry))
        {
            if (ReferenceEquals(entry.Record, station) && entry.Prefab == prefab && entry.Intakes == intakes) start = entry.Next;
            _order.Touch(station.m_uid);
        }
        else
        {
            if (_entries.Count >= Capacity) _entries.Remove(_order.RemoveOldest());
            _order.Add(station.m_uid);
        }
        _entries[station.m_uid] = (station, prefab, intakes, (start + 1) % intakes);
        return start;
    }
    internal void Clear() { _entries.Clear(); _order.Clear(); }
}
