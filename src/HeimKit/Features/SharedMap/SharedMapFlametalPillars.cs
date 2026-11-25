using System;
using System.Collections.Generic;
using HeimKit.Core.World;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

// Native IDs are authoritative during play. Valheim renumbers saved ZDOs at world load.
internal sealed class SharedMapFlametalPillars
{
    internal static readonly int Prefab = "LeviathanLava".GetStableHashCode();
    private readonly ZDOMan _world;
    private readonly SharedMapResourceStore _store;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly Dictionary<ZDOID, ResourceDeposit> _bound = new();
    private readonly HashSet<int> _verified = new();
    private readonly int _restoreCount;
    private int _restoreIndex, _offset, _sectorCount = -1;
    internal long Revision { get; private set; }

    internal SharedMapFlametalPillars(ZDOMan world, SharedMapResourceStore store, SharedMapDiagnostics diagnostics)
    {
        _world = world;
        _store = store;
        _diagnostics = diagnostics;
        _restoreCount = store.Pillars.Count;
    }

    internal bool Verified(ResourceDeposit deposit) => _verified.Contains(deposit.Identity);
    internal bool Known(ZDOID id) => _bound.ContainsKey(id);
    internal bool Restoring => _restoreIndex < _restoreCount;

    internal bool Observe(ZDO record, Vector3 point)
    {
        if (Restoring || Known(record.m_uid) || record.GetBool(ZDOVars.s_dead)) return false;
        var deposit = _store.AddPillar(point.x, point.y, point.z);
        Bind(record, deposit);
        _diagnostics.PillarChange(deposit, record.m_uid, "discovered");
        return true;
    }

    private void Bind(ZDO record, ResourceDeposit deposit)
    {
        deposit.PillarBinding = record.m_uid;
        _bound[record.m_uid] = deposit;
        _verified.Add(deposit.Identity);
        Revision++;
    }

    internal bool Retire(ZDOID id, string reason) =>
        _bound.TryGetValue(id, out var deposit) && Retire(deposit, id, reason);

    private bool Retire(ResourceDeposit deposit, ZDOID id, string reason)
    {
        if (deposit.Depleted) return false;
        _store.RetirePillar(deposit);
        _verified.Remove(deposit.Identity);
        Revision++;
        _diagnostics.PillarChange(deposit, id, reason);
        return true;
    }

    // One saved discovery per admitted slice, at most 32 native records. The sector list
    // includes unloaded objects; a scene unload alone cannot establish disappearance.
    internal bool RestoreSlice()
    {
        var deposit = _store.Pillars[_restoreIndex];
        if (deposit.Depleted) { Advance(); return false; }
        ZDO? record = null;
        if (!deposit.PillarBinding.IsNone())
            record = _world.GetZDO(deposit.PillarBinding);
        else
        {
            var sector = ZoneSystem.GetZone(new Vector3(deposit.X, deposit.Y, deposit.Z));
            var records = WorldObjectProvider.BorrowSector(_world, sector);
            int count = records?.Count ?? 0;
            if (_sectorCount != count) { _offset = 0; _sectorCount = count; }
            int end = Math.Min(count, _offset + 32);
            while (_offset < end)
            {
                var candidate = records![_offset++];
                if (!candidate.IsValid() || candidate.GetPrefab() != Prefab || Known(candidate.m_uid)) continue;
                var p = candidate.GetPosition();
                // Native floating motion acts only on Y. X/Z survive serialization.
                if (p.x == deposit.X && p.z == deposit.Z) { record = candidate; break; }
            }
            if (record == null && _offset < count) return false;
        }
        Advance();
        if (record == null || !record.IsValid() || record.GetPrefab() != Prefab || !record.Persistent ||
            record.GetLong(ZDOVars.s_creator) != 0)
            return Retire(deposit, deposit.PillarBinding, "missing_at_restore");
        if (record.GetBool(ZDOVars.s_dead))
            return Retire(deposit, record.m_uid, "dive_at_restore");
        Bind(record, deposit);
        return false;
    }

    private void Advance() { _restoreIndex++; _offset = 0; _sectorCount = -1; }
}
