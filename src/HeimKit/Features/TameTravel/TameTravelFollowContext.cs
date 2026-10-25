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
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelFollowContext : IDisposable
{
    private readonly TerrainContext _terrain = new();
    public bool Prepare(ZDO animal, ZDO actor, float now)
    {
        var records = TameTravelRules.Nearby(animal.GetPosition());
        if (records == null || !_terrain.Prepare(animal.GetPosition(), records, now, 48)) return false;
        bool ready = true;
        foreach (var z in records)
        {
            if ((z.GetPosition() - animal.GetPosition()).sqrMagnitude > 48 * 48 && z != actor && z != animal) continue;
            var prefab = PrefabRegistry.Current.Prefab(z.GetPrefab());
            if (prefab == null) return false;
            bool solid = PrefabRegistry.Current.Solid(z.GetPrefab());
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
