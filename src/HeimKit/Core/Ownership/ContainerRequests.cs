using System;
using System.Collections.Generic;
using HeimKit.Core.Players;
using HeimKit.Core.Native;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;
namespace HeimKit.Core.Ownership;

// Native interactions deferred across an opted-in inventory fence. No grant or inventory mutation is synthesized.
internal sealed class ContainerRequests : IDisposable
{
    private static readonly int Open = "RPC_RequestOpen".GetStableHashCode(), Stack = "RPC_RequestStack".GetStableHashCode(), Take = "RPC_RequestTakeAll".GetStableHashCode();
    private readonly ObjectTransferAuthority _authority;
    private readonly IPlayerPresence _presence;
    private readonly Func<ZDOID, bool> _retained;
    private readonly Action<ZDOID, long, int, string, float>? _reported;
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly Dictionary<ZDOID, Request> _pending = new();
    private readonly List<Request> _work = new();
    private bool _servicing;
    private float _lastTick = float.NegativeInfinity;
    private sealed class Request
    {
        internal OwnershipLease Lease = null!;
        internal ZNetPeer Peer = null!;
        internal ZRoutedRpc.RoutedRPCData Data = null!;
        internal float Started, Barrier;
        internal long Owner;
        internal ZNetPeer? OwnerPeer;
        internal bool Released;
    }
    internal ContainerRequests(ObjectTransferAuthority authority, IPlayerPresence presence, Func<ZDOID, bool> retained,
        Action<ZDOID, long, int, string, float>? reported)
    { _authority = authority; _presence = presence; _retained = retained; _reported = reported; }
    internal bool Contains(ZDOID id) => _pending.ContainsKey(id);
    internal static bool IsRequest(int method) => method == Open || method == Stack || method == Take;
    internal static bool Shape(ZRoutedRpc.RoutedRPCData data, ZNetPeer peer, OwnershipLease lease) =>
        lease.StaticControl && lease.ManualContainerAccess && IsRequest(data.m_methodHash) && data.m_senderPeerID == peer.m_uid &&
        data.m_targetZDO == lease.Id && !lease.Id.IsNone() && data.m_parameters != null && data.m_parameters.Size() == sizeof(long);
    internal static bool Nearby(ZNetPeer peer, ZDO container)
    {
        if (!peer.IsReady() || peer.m_characterID.IsNone() || !container.IsValid()) return false;
        var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid || !NativeCharacterHealth.IsAlive(actor)) return false;
        var position = actor.GetPosition(); var target = container.GetPosition();
        return PlayerWorldScope.Finite(position) && PlayerWorldScope.Finite(target) && (position - target).sqrMagnitude <= 100;
    }
    internal bool Capture(ZNetPeer peer, OwnershipLease lease, ZRoutedRpc.RoutedRPCData data, float now)
    {
        if (!PlayerWorldScope.Finite(now) || !Shape(data, peer, lease) || !Nearby(peer, lease.Record)) return false;
        lease.ManualInteractionRequested = true;
        if (_pending.ContainsKey(lease.Id)) return true;
        if (_pending.Count >= 64) return false;
        var request = new Request { Lease = lease, Peer = peer, Data = data, Started = now };
        _pending.Add(lease.Id, request); Report(request, "queued", now); return true;
    }
    internal void Finish(OwnershipLease lease, bool safe, float now)
    {
        if (!_pending.TryGetValue(lease.Id, out var request) || !ReferenceEquals(request.Lease, lease)) return;
        if (safe) { request.Released = true; return; }
        _pending.Remove(lease.Id); Forget(request); Deny(request); Report(request, "unsafe", now);
    }
    private bool Connected(ZNetPeer peer)
    {
        var peers = _presence.Peers;
        for (int i = 0; i < peers.Count; i++) if (ReferenceEquals(peers[i], peer)) return peer.IsReady();
        return false;
    }
    private ZNetPeer? Owner(long uid)
    {
        var peers = _presence.Peers;
        for (int i = 0; i < peers.Count; i++) if (peers[i].m_uid == uid && peers[i].IsReady()) return peers[i];
        return null;
    }
    internal void Tick(float now)
    {
        if (_servicing || _pending.Count == 0 || !PlayerWorldScope.Finite(now)) return;
        if (now < _lastTick) ModuleScheduler.Current.Forget(this);
        _lastTick = now;
        if (!ModuleScheduler.Current.Due(this, "container_requests", now, .1f)) return;
        _servicing = true; _work.Clear();
        foreach (var request in _pending.Values) _work.Add(request);
        try
        {
            for (int index = 0; index < _work.Count; index++)
            {
                var request = _work[index]; var id = request.Lease.Id; var record = request.Lease.Record;
                if (!_pending.TryGetValue(id, out var current) || !ReferenceEquals(current, request)) continue;
                if (!ReferenceEquals(_world, ZDOMan.instance) || !Connected(request.Peer) || !record.IsValid() || record.m_uid != id ||
                    !ReferenceEquals(_world.GetZDO(id), record) || !Nearby(request.Peer, record))
                { _pending.Remove(id); Forget(request); Deny(request); Report(request, "unavailable", now); continue; }
                if (now < request.Started || now - request.Started > 15)
                { _pending.Remove(id); Forget(request); Deny(request); Report(request, "expired", now); continue; }
                if (!request.Released || _retained(id)) continue;
                long owner = record.GetOwner();
                if (owner == 0) continue;
                if (request.Data.m_methodHash == Open && record.GetInt(ZDOVars.s_inUse) != 0 && owner == request.Peer.m_uid)
                { _pending.Remove(id); Forget(request); Report(request, "already_open", now); continue; }
                ZNetView? view = null; ZNetPeer? remote = null;
                if (owner == ZDOMan.GetSessionID())
                {
                    view = ZNetScene.instance.FindInstance(record); if (view == null) continue;
                }
                else
                {
                    remote = Owner(owner); if (remote == null) continue;
                    if (request.Owner != owner || !ReferenceEquals(request.OwnerPeer, remote))
                    { Forget(request); request.Owner = owner; request.OwnerPeer = remote; }
                    if (request.Barrier == 0) { request.Barrier = _authority.SendBarrier(remote, record, now); continue; }
                    if (!_authority.Echoed(request.Barrier)) continue;
                }
                // Remove before native delivery: nested callbacks or a second tick cannot replay it twice.
                _pending.Remove(id); Forget(request);
                request.Data.m_targetPeerID = owner; request.Data.m_parameters.SetPos(0);
                if (view != null) view.HandleRoutedRPC(request.Data);
                else { var packet = new ZPackage(); request.Data.Serialize(packet); remote!.m_rpc.Invoke("RoutedRPC", packet); }
                Report(request, "replayed", now);
            }
        }
        finally { _work.Clear(); _servicing = false; }
    }
    private void Forget(Request request)
    { if (request.Barrier != 0) _authority.ForgetBarrier(request.Barrier); request.Barrier = 0; }
    private void Deny(Request request)
    {
        if (!ReferenceEquals(_world, ZDOMan.instance) || !Connected(request.Peer)) return;
        string response = request.Data.m_methodHash == Open ? "RPC_OpenResponse" : request.Data.m_methodHash == Stack ? "RPC_StackResponse" : "RPC_TakeAllResponse";
        ZRoutedRpc.instance.InvokeRoutedRPC(request.Peer.m_uid, request.Lease.Id, response, false);
    }
    private void Report(Request request, string status, float now) => _reported?.Invoke(request.Lease.Id, request.Peer.m_uid, request.Data.m_methodHash, status, now);
    public void Dispose()
    {
        ModuleScheduler.Current.Forget(this);
        foreach (var request in _pending.Values) Forget(request);
        _pending.Clear(); _work.Clear();
    }
}
