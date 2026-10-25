using System;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Features.TameTravel;

// Intentional offline access cleanup: only native disk loading is hooked, never
// per-frame world enumeration or hot network deserialization.
internal sealed class TameTravelFollowRecovery : IDisposable
{
    private static TameTravelFollowRecovery? _current;
    private readonly Harmony _hooks = new("heimkit.tametravel.follow_recovery");
    private readonly ManualLogSource _log;
    private int _cleared;
    internal TameTravelFollowRecovery(ManualLogSource log)
    {
        _log = log;
        if (_current != null) throw new InvalidOperationException("Follow recovery is already active.");
        _current = this;
        try
        {
            foreach (string name in new[] { "Load", "LoadOldFormat" })
                _hooks.Patch(AccessTools.Method(typeof(ZDO), name, new[] { typeof(ZPackage), typeof(global::Version.World) })
                    ?? throw new MissingMethodException("ZDO", name), postfix: new HarmonyMethod(typeof(TameTravelFollowRecovery), nameof(Loaded)));
            _hooks.Patch(AccessTools.Method(typeof(ZDOMan), "PrepareSave")
                ?? throw new MissingMethodException("ZDOMan", "PrepareSave"), prefix: new HarmonyMethod(typeof(TameTravelFollowRecovery), nameof(Saving)));
        }
        catch { _current = null; _hooks.UnpatchSelf(); throw; }
    }
    private static void Saving(ZDOMan __instance) => TameTravelFollowState.PrepareSave(__instance);
    private static void Loaded(ZDO __instance)
    {
        var current = _current;
        if (current == null || !__instance.Persistent || !__instance.GetBool(ZDOVars.s_tamed) ||
            __instance.GetBool(ZDOVars.s_dead) || __instance.GetLong(ZDOVars.s_user) != 0) return;
        if (!TameTravelFollowState.Recoverable(__instance.GetPrefab(), __instance.GetString(ZDOVars.s_follow),
            __instance.GetString(TameTravelFollowState.OwnerKey), __instance.GetString(TameTravelFollowState.SessionKey))) return;
        TameTravelFollowState.ClearSaved(__instance);
        Interlocked.Increment(ref current._cleared);
    }
    internal void Report()
    {
        int cleared = Interlocked.Exchange(ref _cleared, 0);
        if (cleared > 0) DiagnosticLog.Write(_log, "TameTravel", $"Cleared stale following on {cleared} animal(s) loaded from disk.");
    }
    public void Dispose()
    { try { Report(); _hooks.UnpatchSelf(); } finally { if (ReferenceEquals(_current, this)) _current = null; TameTravelFollowState.ResetTracking(); } }
}
