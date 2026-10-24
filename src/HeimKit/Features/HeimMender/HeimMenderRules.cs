using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderRules
{
    private readonly PrefabCatalog<float> _prefabs = PrefabRegistry.Current.Catalog<float>("HeimMenderRules._prefabs");
    private readonly PrefabCatalog<bool> _monsters = PrefabRegistry.Current.Catalog<bool>("HeimMenderRules._monsters");
    public bool Monster(int hash)
    {
        if (_monsters.TryGetValue(hash, out bool result)) return result;
        var prefab = PrefabRegistry.Current.Prefab(hash); result = prefab && PrefabRegistry.Component<MonsterAI>(prefab);
        if (_monsters.Count < 4096) _monsters[hash] = result;
        return result;
    }
    public bool Eligible(ZDO zdo, out float health, out float maximum)
    {
        health = maximum = 0;
        if (!zdo.IsValid() || zdo.GetLong(ZDOVars.s_creator) == 0) return false;
        int hash = zdo.GetPrefab();
        if (!_prefabs.TryGetValue(hash, out float baseHealth))
        {
            var prefab = PrefabRegistry.Current.Prefab(hash);
            var piece = prefab ? PrefabRegistry.Component<Piece>(prefab) : null;
            var wear = prefab ? PrefabRegistry.Component<WearNTear>(prefab) : null;
            var view = prefab ? PrefabRegistry.Component<ZNetView>(prefab) : null;
            baseHealth = 0;
            if (piece && wear && view && view.m_persistent && wear.m_staticPosition &&
                (piece.m_category == Piece.PieceCategory.BuildingWorkbench || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth) &&
                !PrefabRegistry.Component<Container>(prefab) && !PrefabRegistry.Component<Ship>(prefab) && !PrefabRegistry.Component<Vagon>(prefab) && !PrefabRegistry.Component<Rigidbody>(prefab)) baseHealth = wear.m_health;
            if (_prefabs.Count < 4096) _prefabs[hash] = baseHealth;
        }
        if (!PlayerWorldScope.Finite(baseHealth) || baseHealth <= 0) return false;
        maximum = baseHealth;
        if (Game.m_worldLevel > 0)
        {
            if (!Game.instance) return false;
            maximum += Game.m_worldLevel * Game.instance.m_worldLevelPieceHPMultiplier * maximum;
        }
        health = zdo.GetFloat(ZDOVars.s_health, maximum);
        return PlayerWorldScope.Finite(maximum) && maximum > 0 && PlayerWorldScope.Finite(health) && health > 0 && PlayerWorldScope.Finite(zdo.GetPosition());
    }
    public static bool OwnerReady(ZDO zdo)
    {
        long owner = zdo.GetOwner();
        if (owner == 0) return false;
        if (owner == ZDOMan.GetSessionID())
        {
            var instance = ZNetScene.instance.FindInstance(zdo.m_uid);
            var view = instance ? instance.GetComponent<ZNetView>() : null;
            return view != null && view.IsValid() && view.GetZDO() == zdo && view.IsOwner() && instance!.GetComponent<WearNTear>();
        }
        var peer = ZNet.instance.GetPeer(owner);
        return peer != null && peer.IsReady() && !peer.m_characterID.IsNone();
    }
}
