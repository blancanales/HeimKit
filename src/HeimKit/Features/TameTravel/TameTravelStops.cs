using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;

namespace HeimKit.Features.TameTravel;

// Consumes the shared transfer fence; it owns command state, not a second
// ownership protocol. A stop never sends the native toggle to an uncertain AI.
internal sealed class TameTravelStops
{
    private sealed class StopLease : OwnershipLease
    { public override ZNetPeer DestinationPeer => PreviousPeer!; }
    private sealed class Stop
    {
        internal OwnershipLease Lease = null!;
        internal string Name = "";
        internal float Started, ResetAt;
        internal bool Reset, Complete;
        internal readonly List<(ZNetPeer Peer, float Barrier)> Echoes = new();
    }
    private readonly ObjectTransferAuthority _authority;
    private readonly TameTravelFollowing _following;
    private readonly IPlayerPresence _presence;
    private readonly ManualLogSource _log;
    private readonly Dictionary<ZDOID, Stop> _stops = new();
    private readonly Dictionary<ZDOID, (ZDO Record, string Name)> _cleanup = new();
    private readonly List<ZDOID> _expired = new();
    internal TameTravelStops(ObjectTransferAuthority authority, TameTravelFollowing following, IPlayerPresence presence, ManualLogSource log)
    { _authority = authority; _following = following; _presence = presence; _log = log; }
    internal bool Contains(ZDOID id) => _stops.ContainsKey(id);
    internal void QueueCleanup(ZDO animal, string name, float now)
    {
        if (_cleanup.Count >= 512 && !_cleanup.ContainsKey(animal.m_uid)) return;
        _cleanup[animal.m_uid] = (animal, name);
        // Detach an existing spacing lease before its own invalid-player tick
        // can release the source drain. Busy portal reservations retry later.
        TryStop(animal, name, now);
    }
    internal bool TryStop(ZDO animal, string name, float now)
    {
        if (_stops.TryGetValue(animal.m_uid, out var existing))
        {
            if (existing.Name != name) return false;
            if (!existing.Complete) return false;
            _stops.Remove(animal.m_uid); return true;
        }
        string follow = animal.GetString(ZDOVars.s_follow);
        if (follow.Length != 0 && follow != name || _stops.Count >= 64) return false;
        var lease = _following.TakeForStop(animal.m_uid);
        if (lease == null)
        {
            if (_authority.Contains(animal.m_uid)) return false;
            lease = new StopLease { Id = animal.m_uid, Record = animal, FollowControl = true };
            if (!_authority.Acquire(lease, now, allowDisconnectedOwner: true)) return false;
        }
        // Pause an established server simulation immediately. Preserve phase 0
        // while an earlier owner's final native record is still being drained.
        if (lease.Phase != 0) lease.Phase = 1;
        _stops.Add(animal.m_uid, new Stop { Lease = lease, Name = name, Started = now });
        return false;
    }
    internal void Tick(float now)
    {
        _expired.Clear();
        foreach (var pair in _stops)
        {
            var stop = pair.Value; var lease = stop.Lease;
            if (stop.Complete) { if (now - stop.ResetAt > 15) _expired.Add(pair.Key); continue; }
            if (!lease.Record.IsValid() || !ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), lease.Record))
            { _authority.Release(pair.Key); _expired.Add(pair.Key); continue; }
            if (now - stop.Started > 12)
            {
                DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command,
                    $"Stay for {stop.Name} could not be confirmed; client refresh or ownership drain timed out.",
                    new (string, object?)[] { ("animal", pair.Key), ("stage", stop.Reset ? "client_refresh" : "owner_drain") }, LogLevel.Warning);
                _authority.Release(pair.Key); _expired.Add(pair.Key); continue;
            }
            if (lease.Phase == 0)
            {
                bool connected = false;
                foreach (var peer in _presence.Peers) if (ReferenceEquals(peer, lease.PreviousPeer) && peer.IsReady()) { connected = true; break; }
                if (connected && (!_authority.Echoed(lease.Barrier) || !lease.SourceStateObserved))
                {
                    if (_authority.Echoed(lease.Barrier))
                    {
                        if (lease.SourceEchoTime == 0) lease.SourceEchoTime = now;
                        if (now - lease.SourceEchoTime >= 2) _authority.RequestSourceState(lease, now);
                    }
                    continue;
                }
                lease.Phase = 1;
            }
            if (!stop.Reset)
            {
                string follow = lease.Record.GetString(ZDOVars.s_follow);
                if (follow.Length != 0 && follow != stop.Name)
                { _authority.Release(pair.Key); _expired.Add(pair.Key); continue; }
                if (!TameTravelFollowState.ClearLive(lease.Record, forget: false)) continue;
                foreach (var peer in _presence.Peers)
                    if (peer.IsReady() && peer.m_uid != ZDOMan.GetSessionID())
                        stop.Echoes.Add((peer, _authority.ResetClientInstance(peer, lease, now)));
                stop.Reset = true; stop.ResetAt = now;
            }
            bool echoed = now - stop.ResetAt >= .5f;
            foreach (var echo in stop.Echoes)
            {
                bool connected = false;
                foreach (var peer in _presence.Peers) if (ReferenceEquals(peer, echo.Peer) && peer.IsReady()) { connected = true; break; }
                if (connected && !_authority.Echoed(echo.Barrier)) { echoed = false; break; }
            }
            if (!echoed || lease.Record.GetString(ZDOVars.s_follow).Length != 0) continue;
            TameTravelFollowState.Forget(lease.Record);
            _authority.Release(pair.Key);
            stop.Complete = true; stop.ResetAt = now;
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command, $"Cleared native following for {stop.Name}.",
                new (string, object?)[] { ("animal", pair.Key), ("client_caches", stop.Echoes.Count) });
        }
        foreach (var id in _expired) _stops.Remove(id);
        _expired.Clear();
        foreach (var pair in _cleanup)
        {
            var record = pair.Value.Record;
            if (!record.IsValid() || !ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), record) ||
                record.GetString(ZDOVars.s_follow).Length != 0 && record.GetString(ZDOVars.s_follow) != pair.Value.Name ||
                TryStop(record, pair.Value.Name, now)) _expired.Add(pair.Key);
        }
        foreach (var id in _expired) _cleanup.Remove(id);
    }
    internal void ClearPendingForShutdown()
    {
        foreach (var stop in _stops.Values) if (!stop.Complete) ClearForShutdown(stop.Lease.Record, stop.Name);
        foreach (var pending in _cleanup.Values) ClearForShutdown(pending.Record, pending.Name);
        _stops.Clear(); _cleanup.Clear();
    }
    internal void ClearForShutdown(ZDO animal, string name)
    {
        if (!animal.IsValid() || !ReferenceEquals(ZDOMan.instance.GetZDO(animal.m_uid), animal)) return;
        string follow = animal.GetString(ZDOVars.s_follow);
        if (follow.Length != 0 && follow != name) return;
        // No native toggle needs a living player. The saved provenance remains
        // sufficient for startup recovery if shutdown interrupts replication.
        _following.Stop(animal.m_uid, "session_ended");
        _authority.Release(animal.m_uid);
        animal.SetOwner(ZDOMan.GetSessionID());
        TameTravelFollowState.ClearLive(animal);
        // On shutdown an asynchronous acknowledgement cannot be awaited. Drop
        // live remote caches before returning the record to native assignment.
        var lease = new StopLease { Id = animal.m_uid, Record = animal, FollowControl = true };
        if (_authority.Acquire(lease, UnityEngine.Time.unscaledTime, allowDisconnectedOwner: true))
        {
            foreach (var peer in _presence.Peers)
                if (peer.IsReady() && peer.m_uid != ZDOMan.GetSessionID())
                    _authority.ResetClientInstance(peer, lease, UnityEngine.Time.unscaledTime);
            _authority.Release(animal.m_uid);
        }
        ZDOMan.instance.ForceSendZDO(animal.m_uid);
        animal.SetOwner(0);
    }
}
