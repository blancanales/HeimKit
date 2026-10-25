using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

// Persist provenance on the original animal so interrupted sessions can be
// cleaned during native world loading, before clients can restore following.
internal static class TameTravelFollowState
{
    internal static readonly int OwnerKey = "heimkit_tametravel_follow_owner".GetStableHashCode();
    internal static readonly int SessionKey = "heimkit_tametravel_follow_session".GetStableHashCode();
    internal static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly Dictionary<ZDOID, (ZDO Record, string Name)> Tracked = new();
    internal static void Untrack(ZDOID id) => Tracked.Remove(id);
    internal static void ResetTracking() => Tracked.Clear();
    private static readonly int Wolf = "Wolf".GetStableHashCode();
    private static readonly int[] Legacy = { "Boar".GetStableHashCode(), "Lox".GetStableHashCode(),
        "Asksvin".GetStableHashCode(), "Hen".GetStableHashCode(), "Moose".GetStableHashCode() };

    internal static void Mark(ZDO animal, string name)
    {
        Tracked[animal.m_uid] = (animal, name);
        animal.Set(OwnerKey, name); animal.Set(SessionKey, Session);
    }
    // Native client payloads contain the entire extra-data map. Reassert only
    // our bounded provenance before the native immutable save snapshot is made.
    internal static void PrepareSave(ZDOMan world)
    {
        foreach (var pair in Tracked)
        {
            var record = pair.Value.Record;
            if (!record.IsValid() || record.m_uid != pair.Key || !ReferenceEquals(world.GetZDO(pair.Key), record)) continue;
            string follow = record.GetString(ZDOVars.s_follow);
            if (follow.Length != 0 && follow != pair.Value.Name) continue;
            record.Set(OwnerKey, pair.Value.Name); record.Set(SessionKey, Session);
        }
    }
    internal static void Forget(ZDO animal)
    {
        Tracked.Remove(animal.m_uid);
        animal.Set(OwnerKey, ""); animal.Set(SessionKey, Session);
    }

    internal static bool Recoverable(int prefab, string follow, string owner, string session)
    {
        bool supported = prefab == Wolf;
        foreach (int hash in Legacy) supported |= hash == prefab;
        if (!supported) return false;
        if (owner.Length != 0) return session != Session && (follow.Length == 0 || follow == owner);
        // Earlier versions had no provenance. Non-commandable supported tames
        // have no vanilla follow interaction; never infer ownership of a wolf.
        if (session.Length != 0 || follow.Length == 0 || prefab == Wolf) return false;
        foreach (int hash in Legacy) if (hash == prefab) return true;
        return false;
    }

    internal static void ClearSaved(ZDO animal, bool forget = true)
    {
        animal.Set(ZDOVars.s_follow, "");
        animal.Set(ZDOVars.s_patrolPoint, animal.GetPosition());
        animal.Set(ZDOVars.s_patrol, true);
        if (forget) Forget(animal);
    }

    internal static bool ClearLive(ZDO animal, bool forget = true)
    {
        var instance = ZNetScene.instance == null ? null : ZNetScene.instance.FindInstance(animal);
        var ai = instance == null ? null : instance.GetComponent<MonsterAI>();
        if (ai != null) { ai.SetFollowTarget(null); ai.SetPatrolPoint(); ai.StopMoving(); }
        ClearSaved(animal, forget);
        return ai == null || ai.GetFollowTarget() == null;
    }
}
