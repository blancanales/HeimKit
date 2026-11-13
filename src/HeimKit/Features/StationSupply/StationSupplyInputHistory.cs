using System;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Features.StationSupply.Models;
namespace HeimKit.Features.StationSupply;

// Bounded feeding decisions, not an inventory/prefab cache. Native revisions invalidate misses.
internal sealed class StationSupplyInputHistory
{
    internal const int Capacity = 256;
    private readonly Dictionary<(ZDOID Chest, SupplyIntake Intake), int> _slots = new();
    private readonly Failure[] _entries = new Failure[Capacity];
    private int _next;
    internal int Count => _slots.Count;
    private struct Failure
    {
        internal ZDO? Record;
        internal ZDOID Chest;
        internal SupplyIntake? Intake;
        internal uint Revision;
        internal InventoryReadEvidence Evidence;
    }
    internal bool TryGet(ZDO chest, SupplyIntake intake, out InventoryReadEvidence evidence)
    {
        evidence = default;
        var key = (chest.m_uid, intake);
        if (!_slots.TryGetValue(key, out int slot)) return false;
        var entry = _entries[slot];
        if (!ReferenceEquals(entry.Record, chest) || !chest.IsValid() || entry.Revision != chest.DataRevision)
        { _slots.Remove(key); _entries[slot] = default; return false; }
        evidence = entry.Evidence; return true;
    }
    internal void Remember(ZDO chest, SupplyIntake intake, uint parsedRevision, byte[]? parsedInventory, InventoryReadEvidence evidence)
    {
        if ((evidence.Status != "no_compatible_resource" && evidence.Status != "no_allowed_item") || parsedInventory == null ||
            !chest.IsValid() || chest.DataRevision != parsedRevision || !ReferenceEquals(chest.GetByteArray(ZDOVars.s_items), parsedInventory)) return;
        var key = (chest.m_uid, intake);
        if (!_slots.TryGetValue(key, out int slot))
        {
            slot = _next; _next = (_next + 1) % Capacity;
            var previous = _entries[slot];
            if (previous.Intake != null) _slots.Remove((previous.Chest, previous.Intake));
            _slots.Add(key, slot);
        }
        _entries[slot] = new Failure { Record = chest, Chest = chest.m_uid, Intake = intake, Revision = parsedRevision, Evidence = evidence };
    }
    internal void Clear() { _slots.Clear(); Array.Clear(_entries, 0, _entries.Length); _next = 0; }
}
