using System;
using HeimKit.Core.Prefabs;
using System.Collections.Generic;
using UnityEngine;
namespace HeimKit.Features.StationSupply;
internal static class StationSupplyAccess
{
    private static readonly int[] PermissionKeys = CreatePermissionKeys();
    private static int[] CreatePermissionKeys()
    {
        var keys = new int[128];
        for (int i = 0; i < keys.Length; i++) keys[i] = ("pu_id" + i).GetStableHashCode();
        return keys;
    }
    internal static bool Allowed(IReadOnlyList<ZDO> records, PrefabCatalog<float> wards, long creator, Vector3 station, Vector3 chest)
    {
        if (creator == 0) return false;
        for (int index = 0; index < records.Count; index++)
        {
            var ward = records[index];
            if (!ward.IsValid() || !wards.TryGetValue(ward.GetPrefab(), out float wardRadius) || !ward.GetBool(ZDOVars.s_enabled)) continue;
            if (!Inside(ward.GetPosition(), station, wardRadius) && !Inside(ward.GetPosition(), chest, wardRadius)) continue;
            if (ward.GetLong(ZDOVars.s_creator) == creator) continue;
            int count = ward.GetInt(ZDOVars.s_permitted);
            if (count < 0 || count > 128) return false;
            bool permitted = false;
            for (int i = 0; i < count; i++) if (ward.GetLong(PermissionKeys[i]) == creator) { permitted = true; break; }
            if (!permitted) return false;
        }
        return true;
    }
    private static bool Inside(Vector3 a, Vector3 b, float radius)
    { float x = a.x - b.x, z = a.z - b.z; return x * x + z * z < radius * radius; }
}
