using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using BepInEx.Logging;
using HeimKit.Core.Players;

namespace HeimKit.Core.Ownership;

// Independent controllers share one fence, but retain/release only their own IDs.
// This does not replace native ownership globally or depend on WeatherGuard.
internal sealed class ObjectAuthority : IDisposable
{
    private static readonly List<ObjectAuthority> Consumers = new();
    private static readonly Harmony Hooks = new("heimkit.server_object_authority");
    private static readonly HashSet<ZDOID> InstanceIds = new();
    private static bool HasRecords
    {
        get
        {
            foreach (var consumer in Consumers)
                if (consumer._records.Count != 0 && ReferenceEquals(consumer._world, ZDOMan.instance)) return true;
            return false;
        }
    }
    private readonly Dictionary<ZDOID, ZDO> _records = new();
    private readonly Dictionary<(int X, int Y, int Z), HashSet<ZDOID>> _points = new();
    private readonly Dictionary<ZDOID, (int X, int Y, int Z)> _cells = new();
    private readonly HashSet<int> _commands;
    private readonly Action<Exception> _failed;
    private readonly IPlayerPresence _presence;
    private readonly ManualLogSource _log;
    private static readonly Dictionary<string, (float Next, int Repeats)> PacketIncidents = new();
    private readonly bool _instances;
    private readonly ZDOMan _world = ZDOMan.instance;
    private bool _disposed;
    public bool Contains(ZDOID id) => _records.ContainsKey(id);
    internal static void Assign(ZDO record, long owner) => record.SetOwner(owner);
    public ObjectAuthority(Action<Exception> failed, ManualLogSource log, IPlayerPresence presence, bool instances, params string[] commands)
    {
        _log = log; _presence = presence; _failed = failed; _instances = instances; _commands = new(commands.Select(s => s.GetStableHashCode()));
        if (Consumers.Count == 0)
        {
            try
            {
                foreach (var patch in NativePatches())
                    Hooks.Patch(patch.Target, prefix: new HarmonyMethod(typeof(ObjectAuthority), patch.Prefix));
            }
            catch { Hooks.UnpatchSelf(); throw; }
        }
        Consumers.Add(this);
    }
    // Resolve the complete set before installing anything. Native names can have overloads.
    internal static (MethodInfo Target, string Prefix)[] NativePatches()
    {
        MethodInfo Target(Type type, string name, params Type[] parameters)
            => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, parameters, null) ?? throw new MissingMethodException(type.Name, name);
        return new[]
        {
            (Target(typeof(ZDO), "SetOwner", typeof(long)), nameof(Owner)),
            (Target(typeof(ZDOMan), "RPC_ZDOData", typeof(ZRpc), typeof(ZPackage)), nameof(Data)),
            (Target(typeof(ZRoutedRpc), "RPC_RoutedRPC", typeof(ZRpc), typeof(ZPackage)), nameof(Routed)),
            (Target(typeof(ZNetScene), "CreateObjects", typeof(List<ZDO>), typeof(List<ZDO>)), nameof(Instances)),
            (Target(typeof(ZNetScene), "RemoveObjects", typeof(List<ZDO>), typeof(List<ZDO>)), nameof(Instances)),
            (Target(typeof(ZNetScene), "OutsideActiveArea", typeof(Vector3)), nameof(ActiveArea))
        };
    }
    public void Retain(ZDO zdo)
    {
        if (_disposed || !ReferenceEquals(_world, ZDOMan.instance)) throw new InvalidOperationException("Authority belongs to another world.");
        if (_records.Count >= 4096 && !_records.ContainsKey(zdo.m_uid)) throw new InvalidOperationException("Authority capacity exceeded.");
        _records[zdo.m_uid] = zdo;
        if (_instances && !_cells.ContainsKey(zdo.m_uid))
        {
            var cell = Cell(zdo.GetPosition()); _cells[zdo.m_uid] = cell;
            if (!_points.TryGetValue(cell, out var ids)) _points[cell] = ids = new();
            ids.Add(zdo.m_uid);
        }
        zdo.SetOwner(ZDOMan.GetSessionID()); _world.ForceSendZDO(zdo.m_uid);
    }
    public void Release(ZDOID id)
    {
        if (!_records.TryGetValue(id, out var zdo)) return;
        _records.Remove(id);
        if (_cells.TryGetValue(id, out var cell))
        {
            _cells.Remove(id);
            if (_points.TryGetValue(cell, out var ids)) { ids.Remove(id); if (ids.Count == 0) _points.Remove(cell); }
        }
        if (!ReferenceEquals(_world, ZDOMan.instance) || !zdo.IsValid() || zdo.m_uid != id) return;
        if (zdo.GetOwner() == ZDOMan.GetSessionID()) zdo.SetOwner(0);
        _world.ForceSendZDO(id);
    }
    private static ObjectAuthority? For(ZDOID id)
    {
        foreach (var consumer in Consumers)
            if (ReferenceEquals(consumer._world, ZDOMan.instance) && consumer._records.TryGetValue(id, out var record) && record.IsValid() && record.m_uid == id) return consumer;
        return null;
    }
    private static bool Protected(ZDOID id) => For(id) != null;
    private static void Owner(ZDO __instance, ref long uid)
    { if (Protected(__instance.m_uid)) uid = ZDOMan.GetSessionID(); }
    private static void Reassert(ZDOID id, uint data, ushort owner)
    {
        var guard = For(id); var zdo = guard?._world.GetZDO(id);
        if (zdo == null) return;
        // Fail instead of wrapping native revision counters.
        if (Math.Max(zdo.DataRevision, data) == uint.MaxValue || Math.Max((int)zdo.OwnerRevision, owner) == ushort.MaxValue)
        {
            var error = new InvalidOperationException("Network revision exhausted.");
            guard!._failed(error); throw error;
        }
        zdo.DataRevision = Math.Max(zdo.DataRevision, data) + 1;
        zdo.OwnerRevision = (ushort)(Math.Max((int)zdo.OwnerRevision, owner) + 1);
        guard!._world.ForceSendZDO(id);
    }
    private static bool Data(ZRpc rpc, ref ZPackage pkg)
    {
        if (!HasRecords || !Connected(rpc)) return true;
        try { pkg = ZdoPackets.FilterData(pkg, Protected, out _, Reassert); return true; }
        catch (Exception error) { RejectPacket("zdo_data", error); return false; }
    }
    private static bool Connected(ZRpc rpc, long? sender = null)
    {
        // Authentication uses the existing hub's peer objects via native physical connection identity.
        // This is packet provenance, never a gameplay occupancy census.
        foreach (var consumer in Consumers)
        {
            var peer = PlayerPeerLookup.Ready(consumer._presence.Peers, rpc);
            if (peer != null) return !sender.HasValue || peer.m_uid == sender.Value;
        }
        return false;
    }
    private static bool Routed(ZRpc rpc, ref ZPackage pkg)
    {
        if (!HasRecords || !Connected(rpc)) return true;
        try
        {
            if (!ZdoPackets.RelevantRouted(pkg, Protected)) return true;
            var data = ZdoPackets.ReadRouted(pkg);
            if (!Connected(rpc, data.m_senderPeerID)) return false;
            if (data.m_targetZDO.IsNone())
            {
                var original = ZdoPackets.ReadPackage(data.m_parameters);
                if (data.m_parameters.GetPos() != data.m_parameters.Size()) throw new System.IO.InvalidDataException("Trailing destruction data.");
                var filtered = ZdoPackets.FilterDestroyed(original, Protected, out int blocked);
                if (blocked == 0) return true;
                data.m_parameters = new ZPackage(); data.m_parameters.Write(filtered);
            }
            else
            {
                var guard = For(data.m_targetZDO);
                if (guard == null) return true;
                if (!guard._commands.Contains(data.m_methodHash)) return false;
                data.m_targetPeerID = ZDOMan.GetSessionID();
            }
            var output = new ZPackage(); data.Serialize(output); output.SetPos(0); pkg = output; return true;
        }
        catch (Exception error) { RejectPacket("routed", error); return false; }
    }
    private static bool ActiveArea(Vector3 point, ref bool __result)
    {
        if (!PlayerWorldScope.Finite(point)) return true;
        var cell = Cell(point);
        foreach (var consumer in Consumers)
            if (consumer._points.Count != 0 && consumer._instances && ReferenceEquals(consumer._world, ZDOMan.instance))
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            if (!consumer._points.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var ids)) continue;
                            foreach (var id in ids)
                                if (consumer._records.TryGetValue(id, out var record) && record.IsValid() && record.m_uid == id && (record.GetPosition() - point).sqrMagnitude < 0.001f)
                                { __result = false; return false; }
                        }
        return true;
    }
    private static (int X, int Y, int Z) Cell(Vector3 point)
        => ((int)Math.Floor(point.x * 8), (int)Math.Floor(point.y * 8), (int)Math.Floor(point.z * 8));
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        if (!HasRecords) return;
        var seen = InstanceIds;
        seen.Clear();
        foreach (var record in currentNearObjects) seen.Add(record.m_uid);
        foreach (var c in Consumers)
            if (c._instances && ReferenceEquals(c._world, ZDOMan.instance))
                foreach (var pair in c._records)
                    if (pair.Value.IsValid() && pair.Value.m_uid == pair.Key && seen.Add(pair.Key)) currentNearObjects.Add(pair.Value);
        seen.Clear();
    }
    private static void RejectPacket(string operation, Exception error)
    {
        // An unreadable packet is rejected without disabling independent consumers.
        // There are only two operation keys; retained incident state is bounded.
        float now = Time.unscaledTime;
        PacketIncidents.TryGetValue(operation, out var incident);
        if (now < incident.Next) { PacketIncidents[operation] = (incident.Next, incident.Repeats + 1); return; }
        var logger = Consumers.FirstOrDefault()?._log;
        if (logger != null) DiagnosticLog.Write(logger, "HeimKit", CoreLogTopics.Lifecycle, "Rejected protected-object network data; other packets remain active.", new (string, object?)[] { ("operation", operation), ("error_type", error.GetType().FullName), ("repeats_suppressed", incident.Repeats) }, LogLevel.Error, error: incident.Next == 0 ? error : null);
        PacketIncidents[operation] = (now + 60, 0);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<Exception>();
        foreach (var id in _records.Keys.ToArray()) { try { Release(id); } catch (Exception error) { errors.Add(error); } }
        Consumers.Remove(this);
        if (Consumers.Count == 0) { PacketIncidents.Clear(); try { Hooks.UnpatchSelf(); } catch (Exception error) { errors.Add(error); } }
        if (errors.Count > 0) throw new AggregateException("Authority cleanup failed.", errors);
    }
}
