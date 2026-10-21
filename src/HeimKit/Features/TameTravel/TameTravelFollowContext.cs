using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelFollowContext : IDisposable
{
    private readonly WorldTerrainContext _terrain = new();
    private readonly Dictionary<int, bool> _solid = new();
    public bool Prepare(ZDO animal, ZDO actor, float now)
    {
        var records = TameTravelRules.Nearby(animal.GetPosition());
        if (records == null || !_terrain.Prepare(animal.GetPosition(), records, now, 48)) return false;
        bool ready = true;
        foreach (var z in records)
        {
            if ((z.GetPosition() - animal.GetPosition()).sqrMagnitude > 48 * 48 && z != actor && z != animal) continue;
            var prefab = ZNetScene.instance.GetPrefab(z.GetPrefab());
            if (prefab == null) return false;
            if (!_solid.TryGetValue(z.GetPrefab(), out bool solid))
            {
                solid = prefab.GetComponentsInChildren<Collider>(true).Any(c => c.enabled && !c.isTrigger);
                if (_solid.Count < 4096) _solid[z.GetPrefab()] = solid;
            }
            if (!solid && z != actor && z != animal) continue;
            if (!_terrain.Include(z, now)) return false;
            if (ZNetScene.instance.FindInstance(z) == null) ready = false;
        }
        // Both identities must actually be present, even if a native sector query omitted one.
        if (!_terrain.Include(animal, now) || !_terrain.Include(actor, now)) return false;
        return ready && ZNetScene.instance.FindInstance(animal) != null && ZNetScene.instance.FindInstance(actor) != null;
    }
    public void Dispose() => _terrain.Dispose();
}
