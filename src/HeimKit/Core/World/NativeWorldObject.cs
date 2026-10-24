using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using UnityEngine;
namespace HeimKit.Core.World;
internal static class NativeWorldObject
{
    public static ZDO Create(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        var view = PrefabRegistry.Component<ZNetView>(prefab);
        if (view == null || !view.m_persistent || !PlayerWorldScope.Finite(position)) throw new InvalidOperationException("Invalid persistent native prefab.");
        var zdo = ZDOMan.instance.CreateNewZDO(position, prefab.name.GetStableHashCode());
        zdo.Persistent = view.m_persistent; zdo.Type = view.m_type; zdo.Distant = view.m_distant;
        zdo.SetPrefab(prefab.name.GetStableHashCode()); zdo.SetRotation(rotation);
        if (view.m_syncInitialScale) zdo.Set(ZDOVars.s_scaleHash, prefab.transform.localScale);
        return zdo;
    }
}
