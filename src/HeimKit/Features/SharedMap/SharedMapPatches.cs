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
using UnityEngine;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapPatches : IDisposable
{
    private readonly Harmony _harmony = new("heimkit.sharedmap");
    private IDisposable? _packets;
    [ThreadStatic] private static SharedMapController? _receiving;
    [ThreadStatic] private static int _internalWrite;
    [ThreadStatic] private static ZNetPeer? _receivingPeer;

    private static MethodInfo Target(Type type, string name, params Type[] parameters) => NativeAccess.Target(type, name, parameters);
    private static HarmonyMethod Hook(string name) => new(typeof(SharedMapPatches), name);

    public static void CheckCompatibility(bool playerHosted = false)
    {
        _ = Target(typeof(ZDOMan), "GetAllZDOsWithPrefabIterative", typeof(string), typeof(System.Collections.Generic.List<ZDO>), typeof(int).MakeByRefType());
        _ = Target(typeof(ZDOMan), "RPC_ZDOData", typeof(ZRpc), typeof(ZPackage));
        _ = Target(typeof(ZDOMan), "GetPortalList");
        _ = Target(typeof(ZDO), "Deserialize", typeof(ZPackage));
        _ = Target(typeof(ZDO), "Set", typeof(int), typeof(byte[]));
        _ = Target(typeof(ZDO), "SetPrefab", typeof(int));
        if (playerHosted)
        {
            _ = ReadTarget();
            _ = WriteTarget();
            _ = Target(typeof(MapTable), "RPC_MapData", typeof(long), typeof(ZPackage));
            _ = Target(typeof(Minimap), "AddSharedMapData", typeof(byte[]));
            _ = ExploreTarget();
            _ = RoutedSendTarget();
        }
    }

    private static MethodInfo ReadTarget() =>
        Target(typeof(MapTable), "OnRead", typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool));

    private static MethodInfo WriteTarget() =>
        Target(typeof(MapTable), "OnWrite", typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData));

    private static MethodInfo ExploreTarget() => Target(typeof(Minimap), "Explore", typeof(Vector3), typeof(float));

    private static MethodInfo RoutedSendTarget() => Target(typeof(ZRoutedRpc), "InvokeRoutedRPC",
        typeof(long), typeof(ZDOID), typeof(string), typeof(object[]));

    public void Install(bool playerHosted)
    {
        CheckCompatibility(playerHosted);
        try
        {
            _packets = IncomingPackets.RegisterData<MapPacketScope?>(BeginData, finalizer: EndPacket, failed: PacketFailed);
            _harmony.Patch(Target(typeof(ZDO), "Deserialize", typeof(ZPackage)),
                prefix: Hook(nameof(BeforeDeserialize)), postfix: Hook(nameof(AfterDeserialize)));
            _harmony.Patch(Target(typeof(ZDO), "Set", typeof(int), typeof(byte[])),
                prefix: Hook(nameof(BeforeSet)), postfix: Hook(nameof(AfterSet)));
            _harmony.Patch(Target(typeof(ZDO), "SetPrefab", typeof(int)), postfix: Hook(nameof(PrefabAssigned)));
            _harmony.Patch(Target(typeof(Leviathan), "Leave"), postfix: Hook(nameof(AfterLeviathanLeave)));
            if (playerHosted)
            {
                _harmony.Patch(ReadTarget(), prefix: Hook(nameof(BeforeLocalRead)));
                _harmony.Patch(WriteTarget(), prefix: Hook(nameof(BeforeLocalWrite)));
                _harmony.Patch(ExploreTarget(), postfix: Hook(nameof(AfterLocalExplore)));
                _harmony.Patch(RoutedSendTarget(), prefix: Hook(nameof(BeforeLocalRoutedSend)));
            }
        }
        catch { Dispose(); throw; }
    }

    private static void AfterLeviathanLeave(Leviathan __instance)
    {
        var controller = SharedMapController.Active;
        if (controller?.Running != true) return;
        try
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view != null && view.IsValid() && view.IsOwner()) controller.PillarLeaving(view.GetZDO());
        }
        catch (Exception error) { controller.Fail(error); }
    }

    private static void AfterLocalExplore(Minimap __instance, Vector3 p, float radius)
    {
        var controller = SharedMapController.Active;
        if (controller?.Running != true || !ReferenceEquals(__instance, Minimap.instance)) return;
        try { controller.NativeExploration(p, radius); }
        catch (Exception error) { controller.Fail(error); }
    }

    private static void BeforeLocalRoutedSend(ZDOID targetZDO, string methodName, object[] parameters)
    {
        if (methodName is not ("RPC_ApplyOperation" or "RPC_Damage" or "Hit")) return;
        var controller = SharedMapController.Active;
        if (controller?.Running != true || _receivingPeer != null) return;
        try { controller.NativeMiningAction(targetZDO, methodName, parameters); }
        catch (Exception error) { controller.Fail(error); }
    }

    // The write switch calls the same read overload with showMessage=false.
    // Reconcile at the outer write so it cannot decompress an invalid payload later.
    private static bool BeforeLocalRead(MapTable __instance, Humanoid user, ItemDrop.ItemData item, bool showMessage) =>
        !showMessage || item != null || PrepareLocalInteraction(__instance, user, write: false);

    private static bool BeforeLocalWrite(MapTable __instance, Humanoid user, ItemDrop.ItemData item) =>
        item != null || PrepareLocalInteraction(__instance, user, write: true);

    private static bool PrepareLocalInteraction(MapTable table, Humanoid user, bool write)
    {
        var controller = SharedMapController.Active;
        if (controller?.Running != true || !controller.PlayerHosted || user != Player.m_localPlayer) return true;
        try
        {
            if (write && !controller.AllowsPlayerWrites)
            {
                user.Message(MessageHud.MessageType.Center, "SharedMap: Map writes are disabled.");
                return false;
            }

            var view = table.GetComponent<ZNetView>();
            var record = view != null && view.IsValid() ? view.GetZDO() : null;
            return record != null && controller.Reconcile(record, write ? "host_table_write" : "host_table_read", nativeRead: true);
        }
        catch (Exception error) { controller.Fail(error); return false; }
    }

    private static void PacketFailed(Exception error) => SharedMapController.Active?.Fail(error);
    private static bool BeginData(IncomingPackets.Context context, ref ZPackage packet, out MapPacketScope? state)
    { BeginPacket(context.Rpc, out state); return true; }
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
            else if (prefab == SharedMapFlametalPillars.Prefab && controller.KnownPillar(__instance.m_uid) &&
                _receivingPeer?.m_uid == __instance.GetOwner())
                __state = new MapReplicationSnapshot(controller) { PreviousPrefab = SharedMapFlametalPillars.Prefab };
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
            else if (__state.PreviousPrefab == SharedMapFlametalPillars.Prefab) __state.Controller.PillarLeaving(__instance);
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
            if (controller.PlayerHosted && SharedMapController.Equal(bytes, previous)) return true;
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
        if (ModuleScheduler.Current.Battle.Active || _receiving != null || controller == null || !controller.Running ||
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

    public void Dispose() { try { _packets?.Dispose(); } finally { _packets = null; _harmony.UnpatchSelf(); } }
}
