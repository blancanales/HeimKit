using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using BepInEx.Logging;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.ForestRenewal;

internal sealed class ForestRenewalReplacement : IDisposable
{
    private readonly ObjectAuthority _authority;
    public ForestRenewalReplacement(Action<Exception> failed, ManualLogSource log, IPlayerPresence presence)
        => _authority = new(failed, log, presence, false);
    public void Place(ZDO? stump, GameObject prefab, Vector3 point, long ticks)
    {
        var id = stump?.m_uid ?? ZDOID.None;
        if (stump != null) _authority.Retain(stump);
        try
        {
            // Caller has observed durable acknowledgement of this consumed decision.
            // No file I/O or background-thread game access occurs during world mutation.
            if (stump != null)
            {
                if (ZDOMan.instance.GetZDO(id) != stump || !stump.IsValid()) throw new InvalidOperationException("Renewal stump changed during replacement.");
                // Same native destruction message used by ZDOMan.SendDestroyed. It removes the
                // record synchronously on the server and replicates deletion without chopping loot.
                var package = new ZPackage(); package.Write(1); package.Write(id);
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, "DestroyZDO", package);
                if (ZDOMan.instance.GetZDO(id) != null) throw new InvalidOperationException("Renewal stump removal was not confirmed.");
            }
            var sapling = NativeWorldObject.Create(prefab, point, Quaternion.identity);
            sapling.Set(ZDOVars.s_plantTime, ticks);
            sapling.SetOwner(0); ZDOMan.instance.ForceSendZDO(sapling.m_uid);
        }
        finally { if (!id.IsNone()) _authority.Release(id); }
    }
    public void Dispose() => _authority.Dispose();
}
