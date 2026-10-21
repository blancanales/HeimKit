using System.Collections.Generic;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderRules
{
    private readonly Dictionary<int, float> _prefabs = new();
    private readonly Dictionary<int, bool> _monsters = new();
    public bool Monster(int hash)
    {
        if (_monsters.TryGetValue(hash, out bool result)) return result;
        var prefab = ZNetScene.instance.GetPrefab(hash); result = prefab && prefab.GetComponent<MonsterAI>();
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
            var prefab = ZNetScene.instance.GetPrefab(hash);
            var piece = prefab ? prefab.GetComponent<Piece>() : null;
            var wear = prefab ? prefab.GetComponent<WearNTear>() : null;
            var view = prefab ? prefab.GetComponent<ZNetView>() : null;
            baseHealth = 0;
            if (piece && wear && view && view.m_persistent && wear.m_staticPosition &&
                (piece.m_category == Piece.PieceCategory.BuildingWorkbench || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth) &&
                !prefab!.GetComponent<Container>() && !prefab.GetComponent<Ship>() && !prefab.GetComponent<Vagon>() && !prefab.GetComponent<Rigidbody>()) baseHealth = wear.m_health;
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
            return view && view.IsValid() && view.GetZDO() == zdo && view.IsOwner() && instance!.GetComponent<WearNTear>();
        }
        var peer = ZNet.instance.GetPeer(owner);
        return peer != null && peer.IsReady() && !peer.m_characterID.IsNone();
    }
}
