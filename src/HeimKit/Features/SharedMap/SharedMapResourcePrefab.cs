using System;
using System.Linq;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

// Read prefab metadata only. Never instantiate a deposit or run its owner-side damage code.
internal sealed class SharedMapResourcePrefab
{
    public string Name { get; }
    public ResourceKind Kind { get; }
    public int Hash { get; }
    public bool ReplacesWithMining { get; }
    private readonly int _sections;
    private readonly bool _mineRock, _mineRock5;
    private readonly float _health;

    private SharedMapResourcePrefab(string name, ResourceKind kind, bool replacement, int sections,
        bool mineRock, bool mineRock5, float health)
    {
        Name = name; Kind = kind; Hash = name.GetStableHashCode(); ReplacesWithMining = replacement;
        _sections = sections; _mineRock = mineRock; _mineRock5 = mineRock5; _health = health;
    }

    public static SharedMapResourcePrefab? Read(string name, ResourceKind kind, string drop, string? successor)
    {
        var prefab = ZNetScene.instance.GetPrefab(name);
        if (prefab == null || prefab.GetComponent<Piece>() != null || prefab.GetComponent<ItemDrop>() != null ||
            prefab.GetComponent<Character>() != null || prefab.GetComponent<Leviathan>() != null ||
            prefab.GetComponent<ZNetView>()?.m_persistent != true) return null;
        var mine = prefab.GetComponent<MineRock>();
        var mine5 = prefab.GetComponent<MineRock5>();
        var destructible = prefab.GetComponent<Destructible>();
        bool replacement = successor != null;
        SharedMapResourcePrefab? miningSuccessor = null;
        if (replacement)
        {
            if (destructible == null || destructible.m_spawnWhenDestroyed == null ||
                destructible.m_spawnWhenDestroyed.name != successor) return null;
            miningSuccessor = Read(successor!, kind, drop, null);
            if (miningSuccessor == null) return null;
        }
        else
        {
            var drops = mine != null ? mine.m_dropItems : mine5 != null ? mine5.m_dropItems : prefab.GetComponent<DropOnDestroyed>()?.m_dropWhenDestroyed;
            if (drops == null || !drops.m_drops.Any(d => d.m_item != null && d.m_item.name == drop)) return null;
        }
        int sections = mine != null ? (mine.m_areaRoot != null ? mine.m_areaRoot : prefab).GetComponentsInChildren<Collider>().Length :
            mine5 != null ? prefab.GetComponentsInChildren<Collider>().Length : miningSuccessor?._sections ?? 1;
        float health = mine != null ? mine.m_health : mine5 != null ? mine5.m_health : destructible != null ? destructible.m_health : float.NaN;
        if (sections < 1 || sections > 8192 || !SharedMapExploration.Finite(health) || health <= 0 ||
            mine == null && mine5 == null && destructible == null) return null;
        return new SharedMapResourcePrefab(name, kind, replacement, sections, mine != null, mine5 != null, health);
    }

    public MiningProgress InitialProgress => new MiningProgress(_sections, _sections);
    public float HealthBaseline(ZDO zdo) => zdo.GetFloat(ZDOVars.s_health, _health);

    public bool? Depleted(ZDO zdo)
    {
        var progress = Progress(zdo);
        return progress.HasValue ? progress.Value.RemainingSections == 0 : (bool?)null;
    }

    public MiningProgress? Progress(ZDO zdo)
    {
        if (_mineRock5) return SharedMapResourceHealth.Progress(zdo.GetString(ZDOVars.s_health), _sections);
        if (_mineRock)
        {
            int remaining = 0;
            for (int i = 0; i < _sections; i++)
            {
                float health = zdo.GetFloat("Health" + i, _health);
                if (!SharedMapExploration.Finite(health)) return null;
                if (health > 0) remaining++;
            }
            return new MiningProgress(remaining, _sections);
        }
        float value = zdo.GetFloat(ZDOVars.s_health, _health);
        if (!SharedMapExploration.Finite(value)) return null;
        // Exhausting an intact shell is a handoff to its mining successor, not depletion.
        return value <= 0 && ReplacesWithMining ? (MiningProgress?)null : new MiningProgress(value > 0 ? _sections : 0, _sections);
    }
}
