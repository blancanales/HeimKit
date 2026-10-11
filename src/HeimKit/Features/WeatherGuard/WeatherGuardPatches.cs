using HeimKit.Features.WeatherGuard.Models;
using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HeimKit.Features.WeatherGuard;

internal sealed class WeatherGuardPatches : IDisposable
{
    private readonly Harmony _harmony = new("heimkit.weatherguard");
    [ThreadStatic] private static int _protectedWearDepth;
    private static readonly int Destroyed = "DestroyZDO".GetStableHashCode();
    public void Install()
    {
        Patch(typeof(ZoneSystem), "Update", null, nameof(Terrain));
        Patch(typeof(ZNetScene), "CreateObjects", nameof(Instances));
        Patch(typeof(ZNetScene), "RemoveObjects", nameof(Instances));
        Patch(typeof(ZDO), "SetOwner", nameof(Owner));
        Patch(typeof(ZDOMan), "RPC_ZDOData", nameof(Data));
        Patch(typeof(ZRoutedRpc), "RPC_RoutedRPC", nameof(Routed));
        var wear = AccessTools.Method(typeof(WearNTear), "UpdateWear", new[] { typeof(float) });
        if (wear == null) throw new MissingMethodException("WearNTear.UpdateWear");
        _harmony.Patch(wear, prefix: Method(nameof(Wear)), finalizer: Method(nameof(RestoreWear)));
        var outside = AccessTools.Method(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(UnityEngine.Vector3) });
        if (outside == null) throw new MissingMethodException("ZNetScene.OutsideActiveArea");
        _harmony.Patch(outside, prefix: Method(nameof(ActiveArea)));
    }
    private static HarmonyMethod Method(string name) => new(typeof(WeatherGuardPatches), name);
    private void Patch(Type type, string name, string? prefix, string? postfix = null)
    {
        var target = AccessTools.Method(type, name);
        if (target == null) throw new MissingMethodException(type.Name + "." + name);
        _harmony.Patch(target, prefix: prefix == null ? null : Method(prefix), postfix: postfix == null ? null : Method(postfix));
    }
    private static void Terrain()
    {
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return;
        try { active.LoadTerrain(); } catch (Exception error) { active.Fail(error); }
    }
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return;
        try { active.AddInstances(currentNearObjects, currentDistantObjects); } catch (Exception error) { active.Fail(error); }
    }
    private static void Owner(ZDO __instance, ref long uid)
    {
        var active = WeatherGuardController.Active;
        if (active == null || !active.Protects(__instance.m_uid) || uid == active.ServerId) return;
        active.OwnerBlocked(); uid = active.ServerId;
    }
    private static bool Data(ZRpc rpc, ref ZPackage pkg)
    {
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return true;
        // Only incoming connected client packets; server-generated updates are untouched.
        if (!Connected(rpc, null)) return true;
        try
        {
            var filtered = WeatherGuardNetworkFilter.FilterData(pkg, active.Protects, out int blocked, active.Reassert);
            if (blocked != 0) { pkg = filtered; active.DataBlocked(blocked); }
            return true;
        }
        catch (Exception error) { active.Fail(error); return false; }
    }
    private static bool Connected(ZRpc rpc, long? sender)
    {
        foreach (var peer in ZNet.instance.GetPeers())
            if (ReferenceEquals(peer.m_rpc, rpc)) return peer.IsReady() && (!sender.HasValue || peer.m_uid == sender.Value);
        return false;
    }
    private static bool Routed(ZRpc rpc, ref ZPackage pkg)
    {
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return true;
        int cursor = pkg.GetPos();
        try
        {
            // Inspect only the routing header first. Unrelated vanilla RPCs (such as
            // large map transfers) must not inherit WeatherGuard's payload limits.
            if (!WeatherGuardNetworkFilter.RelevantRouted(pkg, active.Protects)) return true;
            if (pkg.Size() > WeatherGuardNetworkFilter.MaximumPacketBytes) throw new System.IO.InvalidDataException("Oversized routed packet.");
            var data = new ZRoutedRpc.RoutedRPCData(); data.Deserialize(pkg);
            pkg.SetPos(cursor);
            if (!Connected(rpc, null)) return true;
            if (!Connected(rpc, data.m_senderPeerID)) { active.BroadcastBlocked(); return false; }
            bool changed = false;
            if (data.m_targetZDO.IsNone() && data.m_methodHash == Destroyed)
            {
                var destroyed = data.m_parameters.ReadPackage();
                var filtered = WeatherGuardNetworkFilter.FilterDestroyed(destroyed, active.Protects, out int blocked, active.Resend);
                if (blocked == 0) return true;
                data.m_parameters = new ZPackage(); data.m_parameters.Write(filtered);
                active.BroadcastBlocked(blocked); changed = true;
            }
            else if (active.Protects(data.m_targetZDO))
            {
                var routing = WeatherGuardRules.Route(data.m_methodHash, data.m_targetPeerID, active.ServerId);
                if (routing == Routing.Drop)
                { active.BroadcastBlocked(); return false; }
                // Requests captured during ownership handover must reach the new owner.
                // Keep other clients' visual broadcasts unchanged.
                if (routing == Routing.Server)
                { data.m_targetPeerID = active.ServerId; active.CommandRouted(); changed = true; }
            }
            if (changed) { var output = new ZPackage(); data.Serialize(output); output.SetPos(0); pkg = output; }
            return true;
        }
        catch (Exception error) { pkg.SetPos(cursor); active.Fail(error); return false; }
    }
    private static void Wear(WearNTear __instance, out bool? __state)
    {
        __state = null;
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return;
        var view = __instance.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner() || !active.Protects(view.GetZDO().m_uid)) return;
        __state = __instance.m_noRoofWear;
        __instance.m_noRoofWear = false;
        _protectedWearDepth++; active.WearPass();
        // Do not skip UpdateWear: combat, support and other vanilla branches still run.
    }
    private static Exception? RestoreWear(WearNTear __instance, bool? __state, Exception? __exception)
    {
        if (__state.HasValue)
        { __instance.m_noRoofWear = __state.Value; _protectedWearDepth = Math.Max(0, _protectedWearDepth - 1); }
        if (__exception != null) WeatherGuardController.Active?.Fail(__exception);
        return __exception;
    }
    private static bool ActiveArea(ref bool __result)
    {
        if (_protectedWearDepth == 0 || WeatherGuardController.Active?.Running != true) return true;
        __result = false; return false;
    }
    public void Dispose() { _harmony.UnpatchSelf(); }
}
