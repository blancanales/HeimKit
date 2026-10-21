using System;
using System.Collections.Generic;
using UnityEngine;
using HeimKit.Shared;
namespace HeimKit.Features.DoorKeeper;

// A native door also owns WearNTear: retain its surrounding native support/roof context.
internal sealed class DoorKeeperContext : IDisposable
{
    private readonly WorldTerrainContext _terrain = new();
    private readonly Dictionary<int, bool> _definitions = new();
    public string? WaitReason => _terrain.WaitReason;
    public ZDOID WaitObject => _terrain.WaitObject;
    public bool Prepare(PlayerWorldScope scope, float now)
    {
        if (!_terrain.Prepare(scope.Center, scope.Records, now)) return false;
        bool ready = true;
        foreach (var record in scope.Records)
        {
            if (!record.IsValid() || Character.InInterior(record.GetPosition())) continue;
            var prefab = ZNetScene.instance.GetPrefab(record.GetPrefab());
            if (prefab == null) { ready = false; continue; }
            if (!_definitions.TryGetValue(record.GetPrefab(), out bool required))
            {
                required = prefab.GetComponent<Character>() == null && prefab.GetComponent<ItemDrop>() == null &&
                    (prefab.GetComponent<Piece>() != null || prefab.GetComponentsInChildren<Collider>(true).Length > 0 ||
                        prefab.GetComponent<TerrainComp>() != null || prefab.GetComponent<TerrainModifier>() != null);
                if (_definitions.Count < 4096) _definitions[record.GetPrefab()] = required;
            }
            if (!required) continue;
            if (!_terrain.Include(record, now)) return false;
            if (ZNetScene.instance.FindInstance(record) == null) ready = false;
        }
        return ready;
    }
    public void Clear() => _terrain.Clear();
    public void Dispose() => _terrain.Dispose();
}
