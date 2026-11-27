using System;
using System.Collections.Generic;
using HarmonyLib;
using HeimKit.Core.Native;

namespace HeimKit.Core.Ownership;

// Native RequestZDO can return a vanilla owner's payload only when its revision
// exceeds the zero revision sent by the host during an ordered source drain.
internal sealed class InitialStaticRevision : IDisposable
{
    private static readonly Harmony Hook = new("heimkit.static_objects.initial_owner");
    private static readonly List<InitialStaticRevision> Registrations = new();
    private readonly Func<ZDO, bool> _eligible;

    internal InitialStaticRevision(Func<ZDO, bool> eligible)
    {
        _eligible = eligible;
        if (Registrations.Count == 0)
            Hook.Patch(NativeAccess.Target(typeof(ZDO), "SetOwner", typeof(long)),
                prefix: new HarmonyMethod(typeof(InitialStaticRevision), nameof(PrepareFirstHandoff)));
        Registrations.Add(this);
    }

    private static void PrepareFirstHandoff(ZDO __instance, long uid)
    {
        if (uid == 0 || __instance.DataRevision != 0 || __instance.OwnerRevision != 0 ||
            __instance.GetOwner() != 0 || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        foreach (var registration in Registrations)
            if (registration._eligible(__instance))
            {
                __instance.DataRevision = 1;
                return;
            }
    }

    public void Dispose()
    {
        if (Registrations.Remove(this) && Registrations.Count == 0) Hook.UnpatchSelf();
    }
}
