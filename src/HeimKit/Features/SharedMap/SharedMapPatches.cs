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

    private static MethodInfo Target(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, parameters, null) ?? throw new MissingMethodException(type.Name + "." + name);
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
        __state = new MapPacketScope(_receiving); _receiving = null;
        var active = SharedMapController.Active;
        if (active == null || !active.Running || !active.IsReadyPeer(rpc)) return;
        _receiving = active; __state.Current = active; active.BeginBatch();
    }

    private static Exception? EndPacket(Exception? __exception, MapPacketScope? __state)
    {
        if (__state == null) return __exception; // Another prefix may have skipped ours.
        var controller = __state.Current; _receiving = __state.Previous;
        try { controller?.EndBatch(); }
        catch (Exception error) { controller?.Fail(error); }
        return __exception; // Preserve native exceptions and nested packet context.
    }

    private static void BeforeDeserialize(ZDO __instance, ZPackage pkg, out TableMapSnapshot? __state)
    {
        __state = null;
        var controller = _receiving;
        if (controller == null || !controller.Running) return;
        int cursor = pkg.GetPos();
        try
        {
            if (pkg.Size() - cursor < 6) return;
            pkg.ReadUShort();
            if (pkg.ReadInt() == SharedMapController.TablePrefab)
                __state = new TableMapSnapshot(controller, __instance.GetByteArray(ZDOVars.s_data));
        }
        finally { pkg.SetPos(cursor); }
    }

    private static void AfterDeserialize(ZDO __instance, TableMapSnapshot? __state)
    {
        if (__state == null || !__state.Controller.Running) return;
        try { __state.Controller.Incoming(__instance, __state.Previous); }
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
            if (!SharedMapController.Equal(bytes, normalized))
                __state = new TableMapSnapshot(controller, previous) { RemovedDeaths = removed, Expected = normalized };
            bytes = normalized;
            return true;
        }
        catch (Exception error) { controller.Fail(error); return false; }
    }

    private static void AfterSet(ZDO __instance, TableMapSnapshot? __state)
    {
        if (__state == null || !__state.Controller.Running) return;
        var actual = __instance.GetByteArray(ZDOVars.s_data);
        if (SharedMapController.Equal(__state.Previous, actual) || !SharedMapController.Equal(__state.Expected, actual)) return;
        try { __state.Controller.RecordChange(__instance.m_uid, __state.RemovedDeaths, "server_map_field_write", __instance.DataRevision); }
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
