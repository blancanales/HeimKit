using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Reflection;
using HarmonyLib;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapPatches : IDisposable
{
    private readonly Harmony _harmony = new("heimkit.sharedmap");
    [ThreadStatic] private static SharedMapController? _receiving;
    [ThreadStatic] private static int _internalWrite;
    [ThreadStatic] private static ZNetPeer? _receivingPeer;

    private static MethodInfo Target(Type type, string name, params Type[] parameters) => NativeAccess.Target(type, name, parameters);
    private static HarmonyMethod Hook(string name) => new(typeof(SharedMapPatches), name);

    public static void CheckCompatibility()
    {
        _ = Target(typeof(ZDOMan), "GetAllZDOsWithPrefabIterative", typeof(string), typeof(System.Collections.Generic.List<ZDO>), typeof(int).MakeByRefType());
        _ = Target(typeof(ZDOMan), "RPC_ZDOData", typeof(ZRpc), typeof(ZPackage));
        _ = Target(typeof(ZDOMan), "GetPortalList");
        _ = Target(typeof(ZDO), "Deserialize", typeof(ZPackage));
        _ = Target(typeof(ZDO), "Set", typeof(int), typeof(byte[]));
        _ = Target(typeof(ZDO), "SetPrefab", typeof(int));
    }

    public void Install()
    {
        CheckCompatibility();
        try
        {
            _harmony.Patch(Target(typeof(ZDOMan), "RPC_ZDOData", typeof(ZRpc), typeof(ZPackage)),
                prefix: Hook(nameof(BeginPacket)), finalizer: Hook(nameof(EndPacket)));
            _harmony.Patch(Target(typeof(ZDO), "Deserialize", typeof(ZPackage)),
                prefix: Hook(nameof(BeforeDeserialize)), postfix: Hook(nameof(AfterDeserialize)));
            _harmony.Patch(Target(typeof(ZDO), "Set", typeof(int), typeof(byte[])),
                prefix: Hook(nameof(BeforeSet)), postfix: Hook(nameof(AfterSet)));
            _harmony.Patch(Target(typeof(ZDO), "SetPrefab", typeof(int)), postfix: Hook(nameof(PrefabAssigned)));
        }
        catch { Dispose(); throw; }
    }

    private static void BeginPacket(ZRpc rpc, out MapPacketScope? __state)
    {
        __state = new MapPacketScope(_receiving, _receivingPeer); _receiving = null; _receivingPeer = null;
        var active = SharedMapController.Active;
        if (active == null || !active.Running) return;
        var peer = active.ReadyPeer(rpc);
        if (peer == null) return;
        _receivingPeer = peer; _receiving = active; __state.Current = active; active.BeginBatch();
    }

    private static Exception? EndPacket(Exception? __exception, MapPacketScope? __state)
    {
        if (__state == null) return __exception; // Another prefix may have skipped ours.
        var controller = __state.Current; _receiving = __state.Previous; _receivingPeer = __state.PreviousPeer;
        try { controller?.EndBatch(); }
        catch (Exception error) { controller?.Fail(error); }
        return __exception; // Preserve native exceptions and nested packet context.
    }

    private static void BeforeDeserialize(ZDO __instance, ZPackage pkg, out MapReplicationSnapshot? __state)
    {
        __state = null;
        var controller = _receiving;
        if (controller == null || !controller.Running) return;
        int cursor = pkg.GetPos();
        try
        {
            if (pkg.Size() - cursor < 6) return;
            pkg.ReadUShort();
            int prefab = pkg.ReadInt();
            if (prefab == SharedMapController.TablePrefab)
                __state = new MapReplicationSnapshot(controller) { Table = new TableMapSnapshot(controller, __instance.GetByteArray(ZDOVars.s_data)) { SenderPeer = _receivingPeer?.m_uid, SenderName = _receivingPeer?.m_playerName ?? "" } };
            else if (controller.ObservesResourceRecord(prefab))
                __state = new MapReplicationSnapshot(controller) {
                    PreviousPrefab = __instance.GetPrefab(), Terrain = __instance.GetByteArray(ZDOVars.s_TCData),
                    MiningString = __instance.GetString(ZDOVars.s_health), MiningHealth = controller.ResourceHealthBaseline(__instance)
                };
        }
        finally { pkg.SetPos(cursor); }
    }

    private static void AfterDeserialize(ZDO __instance, MapReplicationSnapshot? __state)
    {
        if (__state == null || !__state.Controller.Running) return;
        try
        {
            if (__state.Table != null) __state.Controller.Incoming(__instance, __state.Table);
            else __state.Controller.AcceptedResourceRecord(__instance, __state);
        }
        catch (Exception error) { __state.Controller.Fail(error); }
    }

    private static bool BeforeSet(ZDO __instance, int hash, ref byte[] bytes, out TableMapSnapshot? __state)
    {
        __state = null;
        var controller = SharedMapController.Active;
        if (_internalWrite != 0 || controller == null || !controller.Running || hash != ZDOVars.s_data ||
            __instance.GetPrefab() != SharedMapController.TablePrefab) return true;
        try
        {
            var previous = __instance.GetByteArray(hash);
            var normalized = controller.Normalize(bytes, previous, out int removed);
            if (normalized == null) return false;
            if (!SharedMapController.Equal(previous, normalized) || !SharedMapController.Equal(bytes, normalized))
                __state = new TableMapSnapshot(controller, previous) { RemovedDeaths = removed, Received = bytes, Expected = normalized,
                    SenderPeer = _receivingPeer?.m_uid, SenderName = _receivingPeer?.m_playerName ?? "" };
            bytes = normalized;
            return true;
        }
        catch (Exception error) { controller.Fail(error); return false; }
    }

    private static void AfterSet(ZDO __instance, TableMapSnapshot? __state)
    {
        if (__state == null || !__state.Controller.Running) return;
        var actual = __instance.GetByteArray(ZDOVars.s_data);
        if (!SharedMapController.Equal(__state.Expected, actual)) return;
        try { __state.Controller.AcceptedServerWrite(__instance, __state); }
        catch (Exception error) { __state.Controller.Fail(error); }
    }

    private static void PrefabAssigned(ZDO __instance)
    {
        var controller = SharedMapController.Active;
        if (_receiving != null || controller == null || !controller.Running ||
            __instance.GetPrefab() != SharedMapController.TablePrefab) return;
        try { controller.Reconcile(__instance, "table_prefab_assigned"); }
        catch (Exception error) { controller.Fail(error); }
    }

    internal static void Write(ZDO table, byte[]? data)
    {
        _internalWrite++;
        try
        {
            if (data != null) table.Set(ZDOVars.s_data, data);
            else if (table.RemoveByteArray(ZDOVars.s_data))
            {
                table.DataRevision++;
                ZDOMan.instance.SetDirtySector(table);
            }
            ZDOMan.instance.ForceSendZDO(table.m_uid);
        }
        finally { _internalWrite--; }
    }

    public void Dispose() => _harmony.UnpatchSelf();
}
