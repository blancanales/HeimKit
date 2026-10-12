using HeimKit.Features.WeatherGuard.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using HarmonyLib;

namespace HeimKit.Features.WeatherGuard;

internal sealed class WeatherGuardPatches : IDisposable
{
    private readonly Harmony _harmony = new("heimkit.weatherguard");
    [ThreadStatic] private static int _protectedWearDepth;
    [ThreadStatic] private static WearObservation? _wearObservation;
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
        _harmony.Patch(wear, prefix: Method(nameof(Wear)), transpiler: Method(nameof(WeatherDamage)), finalizer: Method(nameof(RestoreWear)));
        var outside = AccessTools.Method(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(UnityEngine.Vector3) });
        if (outside == null) throw new MissingMethodException("ZNetScene.OutsideActiveArea");
        _harmony.Patch(outside, prefix: Method(nameof(ActiveArea)));
        Patch(typeof(WearNTear), "UpdateSupport", null, nameof(Support));
        foreach (string name in new[] { "RPC_Damage", "RPC_Repair", "RPC_Remove" })
            _harmony.Patch(AccessTools.Method(typeof(WearNTear), name) ?? throw new MissingMethodException(name),
                prefix: Method(nameof(Command)), finalizer: Method(nameof(CommandComplete)));
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
        active.OwnerBlocked(__instance, uid); uid = active.ServerId;
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
            var data = WeatherGuardNetworkFilter.ReadRouted(pkg);
            if (!Connected(rpc, null)) return true;
            if (!Connected(rpc, data.m_senderPeerID)) { active.BroadcastBlocked(); return false; }
            bool changed = false;
            if (data.m_targetZDO.IsNone() && data.m_methodHash == Destroyed)
            {
                var destroyed = WeatherGuardNetworkFilter.ReadPackage(data.m_parameters);
                if (data.m_parameters.GetPos() != data.m_parameters.Size()) throw new System.IO.InvalidDataException("Trailing destruction parameters.");
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
    // The first flag read computes rain wetness (also used by native fire handling).
    // Only the second read gates weather DAMAGE. Reject changed IL rather than patching blindly.
    internal static IEnumerable<CodeInstruction> WeatherDamage(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var flag = typeof(WearNTear).GetField(nameof(WearNTear.m_noRoofWear));
        var reads = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.opcode == OpCodes.Ldfld && Equals(pair.instruction.operand, flag)).Select(pair => pair.index).ToArray();
        if (reads.Length != 2 || reads[1] + 1 >= code.Count ||
            (code[reads[1] + 1].opcode != OpCodes.Brfalse && code[reads[1] + 1].opcode != OpCodes.Brfalse_S) ||
            !code.Take(reads[1]).Any(i => i.opcode == OpCodes.Stfld && Equals(i.operand, typeof(WearNTear).GetField("m_rainWet", BindingFlags.Instance | BindingFlags.NonPublic))))
            throw new MissingMethodException("WeatherGuard weather-damage gate does not match audited UpdateWear IL.");
        code.InsertRange(reads[1] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, typeof(WeatherGuardPatches).GetMethod(nameof(AllowWeatherDamage), BindingFlags.Static | BindingFlags.NonPublic)) });
        return code;
    }
    private static bool AllowWeatherDamage(bool native, WearNTear wear)
    {
        if (_wearObservation != null) _wearObservation.GateReached = true;
        var active = WeatherGuardController.Active;
        if (_protectedWearDepth == 0 || active?.Running != true) return native;
        var view = wear.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner() || !active.Protects(view.GetZDO().m_uid)) return native;
        bool suppress;
        try { suppress = active.SuppressWeather(wear, native); active.WeatherGate(wear, suppress); } catch (Exception error) { active.Fail(error); return native; }
        if (_wearObservation != null) _wearObservation.Suppressed = suppress;
        return suppress ? false : native;
    }
    private static void Wear(WearNTear __instance, float time, out WearProcessingState __state)
    {
        __state = default;
        __state.PreviousObservation = _wearObservation;
        _wearObservation = null;
        var active = WeatherGuardController.Active;
        if (active == null || !active.Running) return;
        var view = __instance.GetComponent<ZNetView>();
        if (view == null || !view.IsValid()) return;
        __state.Protected = view.IsOwner() && active.Protects(view.GetZDO().m_uid);
        try { __state.Observation = active.BeginWear(__instance, view, __state.Protected, time); }
        catch (Exception error) { active.Fail(error); __state.Protected = false; return; }
        _wearObservation = __state.Observation;
        if (__state.Protected) { _protectedWearDepth++; active.WearPass(); }
        // Do not skip UpdateWear: combat, support and other vanilla branches still run.
    }
    private static Exception? RestoreWear(WearNTear __instance, WearProcessingState __state, Exception? __exception)
    {
        if (__state.Protected) _protectedWearDepth = Math.Max(0, _protectedWearDepth - 1);
        _wearObservation = __state.PreviousObservation;
        try { WeatherGuardController.Active?.EndWear(__instance, __state.Observation, __exception); }
        catch (Exception error) { WeatherGuardController.Active?.Fail(error); }
        if (__exception != null) WeatherGuardController.Active?.Fail(__exception);
        return __exception;
    }
    private static bool ActiveArea(ref bool __result)
    {
        if (_protectedWearDepth == 0 || WeatherGuardController.Active?.Running != true) return true;
        __result = false; return false;
    }
    private static void Support(WearNTear __instance)
    {
        try { WeatherGuardController.Active?.SupportPass(__instance); }
        catch (Exception error) { WeatherGuardController.Active?.Fail(error); }
    }
    private static void Command(WearNTear __instance, MethodBase __originalMethod, out WearObservation? __state)
    {
        __state = null;
        try { __state = WeatherGuardController.Active?.BeginCommand(__instance, __originalMethod.Name); }
        catch (Exception error) { WeatherGuardController.Active?.Fail(error); }
    }
    private static Exception? CommandComplete(WearNTear __instance, WearObservation? __state, Exception? __exception)
    {
        try { WeatherGuardController.Active?.EndCommand(__instance, __state, __exception); }
        catch (Exception error) { WeatherGuardController.Active?.Fail(error); }
        return __exception;
    }
    public void Dispose() { _harmony.UnpatchSelf(); }
}
