using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Core.Ownership;

// A short-lived fence for original animal IDs. No player ownership is changed.
internal sealed class ObjectTransferAuthority : IDisposable
{
    private static ObjectTransferAuthority? _current;
    private readonly Harmony _hooks = new("heimkit.tametravel.authority");
    private readonly IPlayerPresence _presence;
    private readonly Action<Exception> _failed;
    private readonly Func<ZDOID, bool> _contains;
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly Dictionary<ZDOID, OwnershipLease> _leases = new();
    private readonly Dictionary<float, (ZNetPeer Peer, float Sent, bool Echo)> _barriers = new();
    private readonly Dictionary<Rigidbody, (bool Kinematic, bool Gravity)> _bodies = new();
    private readonly HashSet<ZDOID> _instanceIds = new();
    private readonly List<float> _expiredBarriers = new();
    private static int _nonce = 10000000;
    private bool _disposed;
    public ObjectTransferAuthority(IPlayerPresence presence, Action<Exception> failed)
    {
        ZdoReplication.CheckCompatibility();
        _presence = presence; _failed = failed; _contains = Contains;
        if (_current != null) throw new InvalidOperationException("TameTravel authority is already active.");
        _current = this;
        try
        {
            Patch(typeof(ZDO), "SetOwner", nameof(Owner), typeof(long));
            _hooks.Patch(AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) }), prefix: new HarmonyMethod(typeof(ObjectTransferAuthority), nameof(Data)), postfix: new HarmonyMethod(typeof(ObjectTransferAuthority), nameof(DataApplied)));
            Patch(typeof(ZRoutedRpc), "RPC_RoutedRPC", nameof(Routed), typeof(ZRpc), typeof(ZPackage));
            Patch(typeof(Game), "RPC_Pong", nameof(Pong), typeof(long), typeof(float));
            Patch(typeof(ZNetScene), "CreateObjects", nameof(Instances), typeof(List<ZDO>), typeof(List<ZDO>));
            Patch(typeof(ZNetScene), "RemoveObjects", nameof(Instances), typeof(List<ZDO>), typeof(List<ZDO>));
            Patch(typeof(Character), "CustomFixedUpdate", nameof(Pause), typeof(float));
            // Humanoid does owner work before invoking the patched Character implementation.
            Patch(typeof(Humanoid), "CustomFixedUpdate", nameof(Pause), typeof(float));
            Patch(typeof(MonsterAI), "UpdateAI", nameof(Pause), typeof(float));
            Patch(typeof(Tameable), "Update", nameof(Pause));
            Patch(typeof(Tameable), "TamingUpdate", nameof(Pause));
            Patch(typeof(Procreation), "Procreate", nameof(Pause));
            Patch(typeof(ZSyncTransform), "CustomFixedUpdate", nameof(Pause), typeof(float));
            Patch(typeof(ZSyncTransform), "CustomLateUpdate", nameof(Pause), typeof(float));
            Patch(typeof(ZSyncTransform), "SyncNow", nameof(Pause));
        }
        catch { try { _hooks.UnpatchSelf(); } finally { _current = null; } throw; }
    }
    private void Patch(Type type, string method, string prefix, params Type[] parameters)
        => _hooks.Patch(AccessTools.Method(type, method, parameters) ?? throw new MissingMethodException(type.Name, method), prefix: new HarmonyMethod(typeof(ObjectTransferAuthority), prefix));
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        var current = _current;
        if (current == null || current._leases.Count == 0 || !ReferenceEquals(current._world, ZDOMan.instance)) return;
        var seen = current._instanceIds; seen.Clear();
        foreach (var record in currentNearObjects) seen.Add(record.m_uid);
        foreach (var pair in current._leases)
            if (pair.Value.Record.IsValid() && pair.Value.Record.m_uid == pair.Key &&
                (pair.Value.Moved || ZNetScene.instance.FindInstance(pair.Value.Record) != null) && seen.Add(pair.Key)) currentNearObjects.Add(pair.Value.Record);
        seen.Clear();
    }
    public bool Contains(ZDOID id) => _leases.ContainsKey(id);
    public bool RunningFollow(ZDOID id) => _leases.TryGetValue(id, out var t) && t.FollowControl && t.Phase == 5;
    public bool Holds(OwnershipLease t) => _leases.TryGetValue(t.Id, out var current) && ReferenceEquals(current, t);
    public bool Acquire(OwnershipLease transfer, float now)
    {
        if (_leases.Count >= 64 || Contains(transfer.Id) || _world.GetZDO(transfer.Id) != transfer.Record || !transfer.Record.IsValid()) return false;
        var z = transfer.Record; transfer.PreviousOwner = z.GetOwner(); transfer.Source = z.GetPosition();
        CheckRevisions(z.DataRevision, z.OwnerRevision);
        if (z.DataRevision == 0) return false;
        if (transfer.PreviousOwner != 0 && transfer.PreviousOwner != ZDOMan.GetSessionID())
        {
            transfer.PreviousPeer = _presence.Peers.FirstOrDefault(p => p.IsReady() && p.m_uid == transfer.PreviousOwner);
            if (transfer.PreviousPeer == null) return false;
        }
        _leases.Add(transfer.Id, transfer);
        z.SetOwner(ZDOMan.GetSessionID()); _world.ForceSendZDO(transfer.Id);
        Freeze(z);
        if (transfer.PreviousPeer != null) RequestSourceState(transfer, now);
        else transfer.Phase = 1;
        return true;
    }
    public float SendBarrier(ZNetPeer peer, ZDO? z, float now)
    {
        Prune(now);
        if (_barriers.Count >= 512) return 0;
        if (++_nonce >= 16000000) throw new InvalidOperationException("TameTravel ordering-barrier nonce exhausted.");
        float nonce = -_nonce;
        _barriers.Add(nonce, (peer, now, false));
        if (z != null) Send(peer, z);
        ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_Ping", nonce);
        return nonce;
    }
    public void RequestSourceState(OwnershipLease t, float now)
    {
        CheckRevisions(t.Record.DataRevision, t.Record.OwnerRevision);
        // A strictly newer ownership revision with wire data revision zero changes ownership
        // without overwriting the client's newer cached payload. Native RPC_ZDOData records
        // that zero as the server's known revision; RequestZDO then passes ShouldSend and
        // returns the client's complete current cache even after it has ceased being owner.
        // This is a native record return, separate from the routed ping's ordering echo.
        t.Record.OwnerRevision++;
        t.SourceOwnerRevision = t.Record.OwnerRevision; t.SourceStateObserved = false; t.SourceEchoTime = 0;
        Send(t.PreviousPeer!, t.Record, 0);
        ZRoutedRpc.instance.InvokeRoutedRPC(t.PreviousPeer!.m_uid, "RequestZDO", t.Id);
        t.Barrier = SendBarrier(t.PreviousPeer, null, now);
    }
    public void ObservePong(ZNetPeer peer, float nonce)
    {
        if (_barriers.TryGetValue(nonce, out var b) && ReferenceEquals(peer, b.Peer)) _barriers[nonce] = (b.Peer, b.Sent, true);
    }
    public bool Echoed(float nonce) => _barriers.TryGetValue(nonce, out var b) && b.Echo;
    private void Prune(float now)
    {
        _expiredBarriers.Clear();
        foreach (var pair in _barriers) if (now - pair.Value.Sent > 15) _expiredBarriers.Add(pair.Key);
        foreach (var key in _expiredBarriers) _barriers.Remove(key);
        _expiredBarriers.Clear();
    }
    public void Tick(float now)
    {
        Prune(now);
        foreach (var t in _leases.Values) if (t.FollowControl ? t.Phase != 5 : t.Phase < 3) Freeze(t.Record);
    }
    public void RunFollow(OwnershipLease t)
    {
        if (!t.FollowControl || !_leases.TryGetValue(t.Id, out var current) || !ReferenceEquals(t, current))
            throw new InvalidOperationException("Following lease is no longer held.");
        t.Phase = 5; RestoreBody(t.Record);
    }
    public void PauseFollow(OwnershipLease t)
    { if (t.FollowControl && _leases.ContainsKey(t.Id)) { t.Phase = 6; Freeze(t.Record); } }
    private void Freeze(ZDO z)
    {
        var view = ZNetScene.instance.FindInstance(z); var body = view == null ? null : view.GetComponent<Rigidbody>();
        if (body == null) return;
        if (!_bodies.ContainsKey(body)) _bodies.Add(body, (body.isKinematic, body.useGravity));
        if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        body.isKinematic = true; body.useGravity = false;
    }
    public void Handoff(OwnershipLease t)
    {
        CheckRevisions(t.Record.DataRevision, t.Record.OwnerRevision);
        t.Phase = 3;
        t.Record.SetOwner(t.DestinationPeer.m_uid);
        t.PublishedOwnerRevision = t.Record.OwnerRevision; t.PublishedRevision = t.Record.DataRevision;
        RestoreBody(t.Record); _world.ForceSendZDO(t.Id); Send(t.DestinationPeer, t.Record);
    }
    public void RefreshDestination(OwnershipLease t, float now)
    {
        if (t.Phase != 3 || t.OwnerObserved || now < t.NextDestinationSend || t.DestinationRetries >= 3) return;
        t.NextDestinationSend = now + 2; t.DestinationRetries++;
        Send(t.DestinationPeer, t.Record);
        ZRoutedRpc.instance.InvokeRoutedRPC(t.DestinationPeer.m_uid, "RequestZDO", t.Id);
    }
    private void RestoreBody(ZDO z)
    {
        var body = ZNetScene.instance.FindInstance(z)?.GetComponent<Rigidbody>();
        if (body != null && _bodies.TryGetValue(body, out var b))
        { _bodies.Remove(body); body.isKinematic = b.Kinematic; body.useGravity = b.Gravity; }
    }
    public void Release(ZDOID id, bool keepServerOwner = false, bool resetDestination = false)
    {
        if (!_leases.TryGetValue(id, out var t)) return;
        _leases.Remove(id);
        if (!ReferenceEquals(_world, ZDOMan.instance) || _world.GetZDO(id) != t.Record || !t.Record.IsValid()) return;
        RestoreBody(t.Record);
        if (!keepServerOwner && (t.Record.GetOwner() == ZDOMan.GetSessionID() ||
            resetDestination && t.Moved && !t.FollowControl && t.Record.GetOwner() == t.DestinationPeer.m_uid)) t.Record.SetOwner(0);
        _world.ForceSendZDO(id);
    }
    public static void Send(ZNetPeer peer, ZDO z, uint? wireRevision = null)
        => ZdoReplication.Send(peer, z, wireRevision);
    private static void CheckRevisions(uint data, ushort owner)
    {
        if (data > uint.MaxValue - 32 || owner > ushort.MaxValue - 8)
            throw new InvalidOperationException("TameTravel cannot safely advance exhausted native record revisions.");
    }
    private static void Owner(ZDO __instance, ref long uid)
    {
        var c = _current;
        if (c != null && c._leases.TryGetValue(__instance.m_uid, out var t))
            uid = !t.FollowControl && t.Phase >= 3 ? t.DestinationPeer.m_uid : ZDOMan.GetSessionID();
    }
    private static bool Pause(Component __instance)
    {
        if (_current == null || _current._leases.Count == 0) return true;
        var z = __instance.GetComponent<ZNetView>()?.GetZDO();
        return z == null || _current == null || !_current._leases.TryGetValue(z.m_uid, out var t) ||
            (t.FollowControl ? t.Phase == 5 : t.Phase >= 3);
    }
    private static bool Pong(long sender, float time)
        => _current == null || !_current._barriers.TryGetValue(time, out var b) || b.Peer.m_uid != sender;
    private static bool Data(ZRpc rpc, ref ZPackage pkg, out List<(OwnershipLease Transfer, bool Source, uint Revision)>? __state)
    {
        __state = null;
        var c = _current;
        if (c == null || c._leases.Count == 0) return true;
        var peer = PlayerPeerLookup.Ready(c._presence.Peers, rpc);
        if (peer == null) return true;
        int cursor = pkg.GetPos();
        try
        {
            if (!ZdoPackets.ContainsData(pkg, c._contains)) return true;
            if (pkg.Size() > ZdoPackets.MaximumPacketBytes) throw new InvalidDataException("Oversized ZDO packet.");
            var output = new ZPackage(); var invalid = new List<ZDOID>();
            int count = pkg.ReadInt();
            if (count < 0 || count > ZdoPackets.MaximumRecords) throw new InvalidDataException("Invalid sector count.");
            for (int i = 0; i < count; i++) { var id = pkg.ReadZDOID(); if (!c.Contains(id)) invalid.Add(id); }
            output.Write(invalid.Count); foreach (var id in invalid) output.Write(id);
            var rejected = new List<(OwnershipLease Transfer, uint Data, ushort Owner)>();
            var drained = new List<(OwnershipLease Transfer, ushort Owner)>();
            var resumed = new List<(OwnershipLease Transfer, bool Source, uint Revision)>(); int records = 0;
            while (true)
            {
                var id = pkg.ReadZDOID(); if (id.IsNone()) break;
                if (++records > ZdoPackets.MaximumRecords) throw new InvalidDataException("Too many ZDO records.");
                ushort ownerRevision = pkg.ReadUShort(); uint revision = pkg.ReadUInt(); long owner = pkg.ReadLong();
                var position = pkg.ReadVector3(); var payload = ZdoPackets.ReadPackage(pkg);
                if (c._leases.TryGetValue(id, out var t))
                {
                    if (c._world.GetZDO(id) != t.Record || t.Record.m_uid != id) continue;
                    bool drain = t.Phase == 0 && ReferenceEquals(peer, t.PreviousPeer) && (owner == t.PreviousOwner || owner == ZDOMan.GetSessionID()) &&
                        PlayerWorldScope.Finite(position) && (position - t.Source).sqrMagnitude <= 144;
                    bool resume = !t.FollowControl && t.Phase >= 3 && ReferenceEquals(peer, t.DestinationPeer) && owner == peer.m_uid &&
                        ownerRevision >= t.PublishedOwnerRevision && revision > t.PublishedRevision && PlayerWorldScope.Finite(position) && (position - t.Placement).sqrMagnitude <= 144;
                    if (!drain && !resume) { rejected.Add((t, revision, ownerRevision)); continue; }
                    if (drain)
                    {
                        CheckRevisions(revision, ownerRevision);
                        if (owner == ZDOMan.GetSessionID() && ownerRevision >= t.SourceOwnerRevision) resumed.Add((t, true, revision));
                        ownerRevision = (ushort)Math.Max(t.Record.OwnerRevision, ownerRevision + 1); owner = ZDOMan.GetSessionID();
                        drained.Add((t, ownerRevision));
                    }
                    else if (revision > t.Record.DataRevision) resumed.Add((t, false, revision));
                }
                output.Write(id); output.Write(ownerRevision); output.Write(revision); output.Write(owner); output.Write(position); output.Write(payload);
            }
            if (pkg.GetPos() != pkg.Size()) throw new InvalidDataException("Trailing ZDO bytes.");
            output.Write(ZDOID.None); output.SetPos(0);
            // No changes until every native record has passed structural validation.
            foreach (var item in drained) item.Transfer.Record.OwnerRevision = item.Owner;
            foreach (var item in rejected)
            {
                var z = item.Transfer.Record;
                if (item.Transfer.Phase == 0) continue; // Do not hide a draining owner's unsent higher revision.
                if (item.Data <= z.DataRevision && item.Owner <= z.OwnerRevision) continue;
                CheckRevisions(Math.Max(item.Data, z.DataRevision), (ushort)Math.Max((int)item.Owner, z.OwnerRevision));
                z.DataRevision = Math.Max(item.Data, z.DataRevision) + 1;
                z.OwnerRevision = (ushort)(Math.Max((int)item.Owner, z.OwnerRevision) + 1);
                c._world.ForceSendZDO(z.m_uid);
            }
            pkg.SetPos(cursor); pkg = output; __state = resumed; return true;
        }
        catch (Exception error) { pkg.SetPos(cursor); c._failed(error); return false; }
    }
    private static void DataApplied(List<(OwnershipLease Transfer, bool Source, uint Revision)>? __state)
    {
        if (__state == null) return;
        foreach (var state in __state)
        {
            var t = state.Transfer;
            if (!t.Record.IsValid() || t.Record.m_uid != t.Id) continue;
            if (state.Source)
            { if (t.Phase == 0 && t.Record.GetOwner() == ZDOMan.GetSessionID() && t.Record.DataRevision >= state.Revision) t.SourceStateObserved = true; }
            else if (t.Record.GetOwner() == t.DestinationPeer.m_uid && t.Record.DataRevision > t.PublishedRevision &&
                (t.Record.GetPosition() - t.Placement).sqrMagnitude <= 144) t.OwnerObserved = true;
        }
    }
    private static bool Routed(ZRpc rpc, ref ZPackage pkg)
    {
        var c = _current;
        if (c == null || c._leases.Count == 0) return true;
        var peer = PlayerPeerLookup.Ready(c._presence.Peers, rpc);
        if (peer == null) return true;
        try
        {
            if (!ZdoPackets.RelevantRouted(pkg, c._contains)) return true;
            var data = ZdoPackets.ReadRouted(pkg);
            if (peer.m_uid != data.m_senderPeerID) return false;
            if (!data.m_targetZDO.IsNone())
            {
                if (!c._leases.TryGetValue(data.m_targetZDO, out var t) || !t.FollowControl || t.Phase != 5) return false;
                // Riding must regain native ownership; never fence the saddle's SetOwner.
                if (data.m_methodHash == "RequestControl".GetStableHashCode()) c.Release(t.Id, keepServerOwner: true);
                data.m_targetPeerID = ZDOMan.GetSessionID();
                var routed = new ZPackage(); data.Serialize(routed); routed.SetPos(0); pkg = routed; return true;
            }
            var destroyed = ZdoPackets.ReadPackage(data.m_parameters);
            if (data.m_parameters.GetPos() != data.m_parameters.Size()) throw new InvalidDataException("Trailing destruction bytes.");
            var filtered = ZdoPackets.FilterDestroyed(destroyed, c._contains, out int blocked);
            if (blocked == 0) return true;
            data.m_parameters = new ZPackage(); data.m_parameters.Write(filtered);
            var output = new ZPackage(); data.Serialize(output); output.SetPos(0); pkg = output; return true;
        }
        catch (Exception error) { c._failed(error); return false; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<Exception>();
        foreach (var id in _leases.Keys.ToArray()) try { Release(id); } catch (Exception error) { errors.Add(error); }
        foreach (var pair in _bodies)
            try { if (pair.Key != null) { pair.Key.isKinematic = pair.Value.Kinematic; pair.Key.useGravity = pair.Value.Gravity; } } catch (Exception error) { errors.Add(error); }
        _bodies.Clear(); _barriers.Clear(); _leases.Clear();
        try { _hooks.UnpatchSelf(); } catch (Exception error) { errors.Add(error); }
        if (ReferenceEquals(_current, this)) _current = null;
        if (errors.Count > 0) throw new AggregateException("TameTravel authority cleanup failed.", errors);
    }
}
