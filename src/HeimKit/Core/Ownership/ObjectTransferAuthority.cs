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

// Per-consumer leases share one native drain/fence implementation. Player ownership is never changed.
internal sealed class ObjectTransferAuthority : IDisposable
{
    private static readonly List<ObjectTransferAuthority> Consumers = new();
    private static SceneListPass _scenePass;
    private static readonly Harmony Hooks = new("heimkit.object_transfer_authority");
    private static IDisposable? _data, _routed;
    private readonly string _name;
    private readonly bool _staticObjects;
    private static readonly Harmony StaticHooks = new("heimkit.static_transfer_authority");
    private readonly IPlayerPresence _presence;
    private readonly Action<Exception> _failed;
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly Dictionary<ZDOID, OwnershipLease> _leases = new();
    private readonly Dictionary<ZDOID, (ZDO Record, ZNetPeer Peer)> _sourceWaits = new();
    private readonly Dictionary<float, (ZNetPeer Peer, float Sent, bool Echo)> _barriers = new();
    private readonly Dictionary<Rigidbody, (bool Kinematic, bool Gravity)> _bodies = new();
    private readonly HashSet<ZDOID> _instanceIds = new();
    private readonly List<float> _expiredBarriers = new();
    private readonly List<ZDOID> _expiredSources = new();
    private readonly ContainerRequests? _containerRequests;
    private static int _nonce = 10000000;
    private bool _disposed;
    public ObjectTransferAuthority(IPlayerPresence presence, Action<Exception> failed, string name = "TameTravel", bool staticObjects = false,
        Action<ZDOID, long, int, string, float>? containerRequestStatus = null)
    {
        ZdoReplication.CheckCompatibility();
        NativeConnection.CheckCompatibility();
        _presence = presence; _failed = failed; _name = name; _staticObjects = staticObjects;
        if (staticObjects) _containerRequests = new(this, presence, Retained, containerRequestStatus);
        if (Consumers.Exists(c => c._name == name)) throw new InvalidOperationException(name + " authority is already active.");
        if (staticObjects && !Consumers.Exists(c => c._staticObjects))
        {
            try
            {
                foreach (var entry in new[] { (typeof(Smelter), "UpdateSmelter"), (typeof(ShieldGenerator), "UpdateShield"), (typeof(Fermenter), "SlowUpdate") })
                    StaticHooks.Patch(NativeAccess.Target(entry.Item1, entry.Item2), prefix: new HarmonyMethod(typeof(ObjectTransferAuthority), nameof(Pause)));
            }
            catch { StaticHooks.UnpatchSelf(); throw; }
        }
        if (Consumers.Count != 0) { Consumers.Add(this); return; }
        try
        {
            Patch(typeof(ZDO), "SetOwner", nameof(Owner), typeof(long));
            _data = IncomingPackets.RegisterData<List<(OwnershipLease Transfer, bool Source, uint Revision)>?>(DataPacket, DataApplied, retained: Retained, failed: PacketFailed, filtering: HasDataLeases);
            _routed = IncomingPackets.RegisterRouted<byte>(RoutedPacket, failed: PacketFailed);
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
            Consumers.Add(this);
        }
        catch { try { _data?.Dispose(); } finally { _data = null; _routed?.Dispose(); _routed = null; Hooks.UnpatchSelf(); if (staticObjects) StaticHooks.UnpatchSelf(); } throw; }
    }
    private void Patch(Type type, string method, string prefix, params Type[] parameters)
        => Hooks.Patch(AccessTools.Method(type, method, parameters) ?? throw new MissingMethodException(type.Name, method), prefix: new HarmonyMethod(typeof(ObjectTransferAuthority), prefix));
    private static ObjectTransferAuthority? For(ZDOID id)
    {
        foreach (var consumer in Consumers)
            if (ReferenceEquals(consumer._world, ZDOMan.instance) && consumer._leases.ContainsKey(id)) return consumer;
        return null;
    }
    private static bool Retained(ZDOID id) => For(id) != null;
    private static bool HasLeases(bool? staticObjects = null)
    {
        foreach (var c in Consumers)
            if ((!staticObjects.HasValue || c._staticObjects == staticObjects.Value) && ReferenceEquals(c._world, ZDOMan.instance) && c._leases.Count != 0) return true;
        return false;
    }
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        if (!HasLeases()) return;
        if (!_scenePass.Enter(Time.frameCount, currentNearObjects, ZDOMan.instance)) return;
        var seen = Consumers[0]._instanceIds; seen.Clear();
        foreach (var record in currentNearObjects) seen.Add(record.m_uid);
        foreach (var current in Consumers)
            if (ReferenceEquals(current._world, ZDOMan.instance))
                foreach (var pair in current._leases)
                    if (pair.Value.Record.IsValid() && pair.Value.Record.m_uid == pair.Key &&
                        (pair.Value.Moved || ZNetScene.instance.FindInstance(pair.Value.Record) != null) && seen.Add(pair.Key)) currentNearObjects.Add(pair.Value.Record);
        seen.Clear();
    }
    public bool Contains(ZDOID id) => _leases.ContainsKey(id);
    public bool RunningFollow(ZDOID id) => _leases.TryGetValue(id, out var t) && t.FollowControl && t.Phase == 5;
    public bool Holds(OwnershipLease t) => _leases.TryGetValue(t.Id, out var current) && ReferenceEquals(current, t);
    public bool Acquire(OwnershipLease transfer, float now, bool allowDisconnectedOwner = false)
        => Acquire(transfer, now, out _, allowDisconnectedOwner);
    public bool Acquire(OwnershipLease transfer, float now, out string reason, bool allowDisconnectedOwner = false)
    {
        ServerPerformance.OwnershipAttempts.Add(0);
        reason = "ownership_acquired";
        if (_containerRequests?.Contains(transfer.Id) == true) { reason = "manual_container_request_pending"; return false; }
        if (_disposed) { reason = "authority_disposed"; return false; }
        if (transfer.StaticControl != _staticObjects) { reason = "authority_kind_mismatch"; return false; }
        if (!ReferenceEquals(_world, ZDOMan.instance)) { reason = "world_changed"; return false; }
        if (_leases.Count >= 64) { reason = "ownership_lease_limit"; return false; }
        if (Retained(transfer.Id)) { reason = "object_already_leased"; return false; }
        if (_world.GetZDO(transfer.Id) != transfer.Record || !transfer.Record.IsValid()) { reason = "object_record_unavailable"; return false; }
        var z = transfer.Record; transfer.PreviousOwner = z.GetOwner(); transfer.Source = z.GetPosition();
        CheckRevisions(z.DataRevision, z.OwnerRevision);
        if (z.DataRevision == 0) { reason = "object_revision_unavailable"; return false; }
        if (_staticObjects && !Preflight(z, out reason)) return false;
        if (_staticObjects && _sourceWaits.Count + _leases.Count >= 256 && !_sourceWaits.ContainsKey(transfer.Id))
        { reason = "source_wait_limit"; return false; }
        if (_staticObjects && _sourceWaits.TryGetValue(transfer.Id, out var waiting))
        { transfer.PreviousPeer = waiting.Peer; transfer.PreviousOwner = waiting.Peer.m_uid; }
        if (transfer.PreviousOwner != 0 && transfer.PreviousOwner != ZDOMan.GetSessionID())
        {
            var peers = _presence.Peers;
            for (int i = 0; i < peers.Count; i++)
                if (peers[i].IsReady() && peers[i].m_uid == transfer.PreviousOwner) { transfer.PreviousPeer = peers[i]; break; }
            if (transfer.PreviousPeer == null && !allowDisconnectedOwner) { reason = "previous_owner_not_ready"; return false; }
            if (transfer.PreviousPeer != null && !NativeConnection.CanSend(transfer.PreviousPeer)) { reason = "previous_owner_transport_unavailable"; return false; }
        }
        _leases.Add(transfer.Id, transfer);
        z.SetOwner(ZDOMan.GetSessionID()); _world.ForceSendZDO(transfer.Id);
        if (!transfer.StaticControl) Freeze(z);
        if (transfer.PreviousPeer != null) RequestSourceState(transfer, now);
        else transfer.Phase = 1;
        return true;
    }
    public float SendBarrier(ZNetPeer peer, ZDO? z, float now)
    {
        if (!CanSend(peer)) return 0;
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
        if (t.PreviousPeer == null || !CanSend(t.PreviousPeer)) return;
        ServerPerformance.SourceRequests.Add(0);
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
    public void ForgetBarrier(float nonce) => _barriers.Remove(nonce);
    public bool Echoed(float nonce) => _barriers.TryGetValue(nonce, out var b) && b.Echo;
    public void TickContainerRequests(float now)
    {
        _containerRequests?.Tick(now);
        if (_sourceWaits.Count == 0 || !ModuleScheduler.Current.Due(this, "source_wait_prune", now, 5)) return;
        _expiredSources.Clear();
        foreach (var pair in _sourceWaits)
            if (!pair.Value.Record.IsValid() || !ReferenceEquals(_world.GetZDO(pair.Key), pair.Value.Record) || pair.Value.Record.m_uid != pair.Key)
                _expiredSources.Add(pair.Key);
        foreach (var id in _expiredSources) _sourceWaits.Remove(id);
        _expiredSources.Clear();
    }
    internal bool CanSend(ZNetPeer peer)
    {
        var peers = _presence.Peers;
        for (int i = 0; i < peers.Count; i++)
            if (ReferenceEquals(peers[i], peer)) return NativeConnection.CanSend(peer);
        return false;
    }
    // Readiness only: callers can inspect both originals before changing either owner.
    internal bool Preflight(ZDO record, out string reason)
    {
        reason = "native_state_ready";
        if (!record.IsValid() || !ReferenceEquals(_world.GetZDO(record.m_uid), record)) { reason = "object_record_unavailable"; return false; }
        if (record.DataRevision == 0) { reason = "object_revision_unavailable"; return false; }
        var source = OwnerConnection(record);
        if (_sourceWaits.ContainsKey(record.m_uid))
        {
            if (source != null && CanSend(source)) return true;
            reason = "previous_source_transport_unavailable"; return false;
        }
        long owner = record.GetOwner();
        if (owner == 0 || owner == ZDOMan.GetSessionID()) return true;
        var peers = _presence.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            if (peer.m_uid == owner)
            {
                if (CanSend(peer)) return true;
                reason = "previous_owner_transport_unavailable"; return false;
            }
        }
        reason = "previous_owner_not_ready"; return false;
    }
    internal ZNetPeer? OwnerConnection(ZDO record)
    {
        long owner = record.GetOwner();
        var peers = _presence.Peers;
        if (_sourceWaits.TryGetValue(record.m_uid, out var waiting))
        {
            bool originalConnected = false;
            for (int i = 0; i < peers.Count; i++) if (ReferenceEquals(peers[i], waiting.Peer)) { originalConnected = true; break; }
            if (!ReferenceEquals(waiting.Record, record) ||
                !originalConnected && owner != 0 && owner != ZDOMan.GetSessionID()) _sourceWaits.Remove(record.m_uid);
            else return waiting.Peer;
        }
        for (int i = 0; i < peers.Count; i++) if (peers[i].m_uid == owner) return peers[i];
        return null;
    }
    public void FinishContainerRequests(OwnershipLease lease, bool safe, float now) => _containerRequests?.Finish(lease, safe, now);
    // Reset only remote caches, never the authoritative world record. The native
    // client destruction handler drops its scene instance before this same-ID
    // snapshot is delivered. Re-creation clears non-replicated AI targets.
    public float ResetClientInstance(ZNetPeer peer, OwnershipLease lease, float now)
    {
        if (!Holds(lease) || lease.Record.GetOwner() != ZDOMan.GetSessionID())
            throw new InvalidOperationException("Client refresh requires fenced server ownership.");
        ZdoReplication.ResetClientCache(lease.Id,
            packet => ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "DestroyZDO", packet), () => Send(peer, lease.Record));
        return SendBarrier(peer, null, now);
    }
    internal float RefreshClientPair(ZNetPeer peer, OwnershipLease station, OwnershipLease chest, bool preserveStation, float now)
    {
        if (!CanSend(peer)) return 0;
        if (!Holds(station) || !Holds(chest) || station.Record.GetOwner() != ZDOMan.GetSessionID() || chest.Record.GetOwner() != ZDOMan.GetSessionID())
            throw new InvalidOperationException("Client refresh requires both fenced originals.");
        ServerPerformance.RefreshGroups.Add(0);
        ZdoReplication.ResetClientPair(station.Id, chest.Id, preserveStation,
            packet => ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "DestroyZDO", packet),
            () => Send(peer, station.Record), () => Send(peer, chest.Record));
        return SendBarrier(peer, null, now);
    }
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
        foreach (var t in _leases.Values) if (!t.StaticControl && (t.FollowControl ? t.Phase != 5 : t.Phase < 3)) Freeze(t.Record);
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
        // Releasing a failed drain must not turn an owner-zero cached snapshot
        // into trusted source state on the next visit. Retain the physical source
        // obligation until drained, replaced, or a new native owner can be drained.
        if (_staticObjects && t.Phase == 0 && !t.SourceStateObserved && t.PreviousPeer != null)
            _sourceWaits[id] = (t.Record, t.PreviousPeer);
        else _sourceWaits.Remove(id);
        RestoreBody(t.Record);
        if (!keepServerOwner && (t.Record.GetOwner() == ZDOMan.GetSessionID() ||
            resetDestination && t.Moved && !t.FollowControl && t.Record.GetOwner() == t.DestinationPeer.m_uid))
            t.Record.SetOwner(_staticObjects && t.Phase == 0 && !t.SourceStateObserved && t.PreviousPeer != null ? t.PreviousOwner : 0);
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
        var c = For(__instance.m_uid);
        if (c != null && c._leases.TryGetValue(__instance.m_uid, out var t))
            uid = !t.StaticControl && !t.FollowControl && t.Phase >= 3 ? t.DestinationPeer.m_uid : ZDOMan.GetSessionID();
    }
    private static bool Pause(Component __instance)
    {
        bool station = __instance is Smelter || __instance is ShieldGenerator || __instance is Fermenter;
        if (!HasLeases(station)) return true;
        var z = __instance.GetComponent<ZNetView>()?.GetZDO();
        var c = z == null ? null : For(z.m_uid);
        return c == null || !c._leases.TryGetValue(z!.m_uid, out var t) ||
            (!t.StaticControl && (t.FollowControl ? t.Phase == 5 : t.Phase >= 3));
    }
    private static bool Pong(long sender, float time)
    {
        foreach (var c in Consumers)
            if (c._barriers.TryGetValue(time, out var b) && b.Peer.m_uid == sender) return false;
        return true;
    }
    private static ObjectTransferAuthority? FirstActive()
    { for (int i = 0; i < Consumers.Count; i++) if (Consumers[i]._leases.Count != 0) return Consumers[i]; return null; }
    private static bool HasDataLeases() => FirstActive() != null;
    private static void PacketFailed(Exception error) => FirstActive()?._failed(error);
    private static bool DataPacket(IncomingPackets.Context context, ref ZPackage packet, out List<(OwnershipLease Transfer, bool Source, uint Revision)>? state)
        => Data(context.Rpc, ref packet, out state);
    private static bool RoutedPacket(IncomingPackets.Context context, ref ZPackage packet, out byte state)
    { state = 0; return Routed(context.Rpc, ref packet); }
    private static bool Data(ZRpc rpc, ref ZPackage pkg, out List<(OwnershipLease Transfer, bool Source, uint Revision)>? __state)
    {
        __state = null;
        var c = FirstActive();
        if (c == null) return true;
        var peer = PlayerPeerLookup.Ready(c._presence.Peers, rpc);
        if (peer == null) return true;
        int cursor = pkg.GetPos();
        try
        {
            if (!IncomingPackets.NeedsDataFilter) return true;
            if (pkg.Size() > ZdoPackets.MaximumPacketBytes) throw new InvalidDataException("Oversized ZDO packet.");
            var output = new ZPackage(); var invalid = new List<ZDOID>();
            int count = pkg.ReadInt();
            if (count < 0 || count > ZdoPackets.MaximumRecords) throw new InvalidDataException("Invalid sector count.");
            for (int i = 0; i < count; i++) { var id = pkg.ReadZDOID(); if (!Retained(id)) invalid.Add(id); }
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
                var consumer = For(id);
                if (consumer != null && consumer._leases.TryGetValue(id, out var t))
                {
                    c = consumer;
                    if (c._world.GetZDO(id) != t.Record || t.Record.m_uid != id) continue;
                    bool drain = t.Phase == 0 && ReferenceEquals(peer, t.PreviousPeer) && (owner == t.PreviousOwner || owner == ZDOMan.GetSessionID()) &&
                        PlayerWorldScope.Finite(position) && (position - t.Source).sqrMagnitude <= (t.StaticControl ? 0.001f : 144);
                    bool resume = !t.StaticControl && !t.FollowControl && t.Phase >= 3 && ReferenceEquals(peer, t.DestinationPeer) && owner == peer.m_uid &&
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
        var c = FirstActive();
        if (c == null) return true;
        var peer = PlayerPeerLookup.Ready(c._presence.Peers, rpc);
        if (peer == null) return true;
        try
        {
            if (!ZdoPackets.RelevantRouted(pkg, Retained)) return true;
            var data = ZdoPackets.ReadRouted(pkg);
            if (peer.m_uid != data.m_senderPeerID) return false;
            if (!data.m_targetZDO.IsNone())
            {
                c = For(data.m_targetZDO);
                if (c == null || !c._leases.TryGetValue(data.m_targetZDO, out var t)) return false;
                if (t.StaticControl)
                {
                    c._containerRequests?.Capture(peer, t, data, Time.unscaledTime);
                    return false;
                }
                if (!t.FollowControl || t.Phase != 5) return false;
                // Riding must regain native ownership; never fence the saddle's SetOwner.
                if (data.m_methodHash == "RequestControl".GetStableHashCode()) c.Release(t.Id, keepServerOwner: true);
                data.m_targetPeerID = ZDOMan.GetSessionID();
                var routed = new ZPackage(); data.Serialize(routed); routed.SetPos(0); pkg = routed; return true;
            }
            var destroyed = ZdoPackets.ReadPackage(data.m_parameters);
            if (data.m_parameters.GetPos() != data.m_parameters.Size()) throw new InvalidDataException("Trailing destruction bytes.");
            var filtered = ZdoPackets.FilterDestroyed(destroyed, Retained, out int blocked);
            if (blocked == 0) return true;
            data.m_parameters = new ZPackage(); data.m_parameters.Write(filtered);
            var output = new ZPackage(); data.Serialize(output); output.SetPos(0); pkg = output; return true;
        }
        catch (Exception error) { c?._failed(error); return false; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _scenePass.Clear();
        ModuleScheduler.Current.Forget(this);
        var errors = new List<Exception>();
        try { _containerRequests?.Dispose(); } catch (Exception error) { errors.Add(error); }
        foreach (var id in _leases.Keys.ToArray()) try { Release(id); } catch (Exception error) { errors.Add(error); }
        foreach (var pair in _bodies)
            try { if (pair.Key != null) { pair.Key.isKinematic = pair.Value.Kinematic; pair.Key.useGravity = pair.Value.Gravity; } } catch (Exception error) { errors.Add(error); }
        _bodies.Clear(); _barriers.Clear(); _leases.Clear(); _sourceWaits.Clear();
        Consumers.Remove(this);
        if (_staticObjects && !Consumers.Exists(c => c._staticObjects)) try { StaticHooks.UnpatchSelf(); } catch (Exception error) { errors.Add(error); }
        if (Consumers.Count == 0)
        {
            try { _data?.Dispose(); } catch (Exception error) { errors.Add(error); } finally { _data = null; }
            try { _routed?.Dispose(); } catch (Exception error) { errors.Add(error); } finally { _routed = null; }
            try { Hooks.UnpatchSelf(); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException(_name + " authority cleanup failed.", errors);
    }
}
