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
using BepInEx.Logging;
using HeimKit.Features.HeimMender.Models;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderController : IDisposable
{
    private static readonly int SayMethod = "Say".GetStableHashCode(), ChatMethod = "ChatMessage".GetStableHashCode();
    private readonly HeimMenderSettings _settings;
    private readonly ManualLogSource _log;
    private readonly PlayerServices _players;
    private readonly PlayerWorldScope _scope;
    private readonly HeimMenderRules _rules = new();
    private readonly HeimMenderDiagnostics _diagnostics;
    private readonly Dictionary<ZDOID, RepairObservation> _observations = new();
    private readonly Dictionary<ZDOID, (ZDO Record, float Seen)> _threats = new();
    private readonly Queue<ZDOID> _queue = new();
    private readonly Dictionary<ZDOID, RepairRequest> _pending = new();
    private readonly Dictionary<long, float> _adminRate = new();
    private readonly System.Random _random = new();
    private readonly IDisposable _messages;
    private readonly List<long> _expiredAdmins = new();
    private readonly List<ZDOID> _expiredRecords = new();
    private readonly List<Vector3> _playerPositions = new(), _threatPositions = new();
    private readonly List<RepairObservation> _candidates = new();
    private readonly ScheduleToken _cycle = new();
    private float _interval;
    private float _threatUnknownUntil;
    private bool _observationCapacity;
    private int _sent, _confirmed, _unconfirmed;
    private string _kind = "";
    public string Status => "HeimMender: queued=" + _queue.Count + ", pending=" + _pending.Count + ", batch=" + (_kind.Length == 0 ? "none" : _kind) + ".";
    public HeimMenderController(HeimMenderSettings settings, ManualLogSource log, PlayerServices players, Action<Exception> fail)
    {
        _settings = settings; _log = log; _players = players; _scope = WorldObjectProvider.Current.CreateScope("HeimMender");
        _diagnostics = new(log, settings.DebugLogging); _interval = settings.IntervalSeconds.Value; _cycle.Postpone(Time.unscaledTime, _interval);
        _messages = players.RoutedMessages.Subscribe(Observe, fail);
        try { _scope.Index(z => _rules.Monster(z.GetPrefab()) || _rules.Eligible(z, out _, out _)); } catch { _messages.Dispose(); throw; }
    }
    public void Tick(float now)
    {
        if (!ModuleScheduler.Current.Due(this, "processing", now, 2)) return;
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _observations.Count) : null;
        if (_interval != _settings.IntervalSeconds.Value) { _interval = _settings.IntervalSeconds.Value; _cycle.Postpone(now, _interval); }
        _expiredAdmins.Clear();
        foreach (var id in _adminRate.Keys)
        {
            bool ready = false;
            for (int rosterIndex1 = 0; rosterIndex1 < _players.Presence.Peers.Count && _players.Presence.Peers[rosterIndex1] is var peer; rosterIndex1++) if (peer.m_uid == id && peer.IsReady()) { ready = true; break; }
            if (!ready) _expiredAdmins.Add(id);
        }
        foreach (var id in _expiredAdmins) _adminRate.Remove(id);
        Reconcile(now);
        _expiredRecords.Clear();
        foreach (var pair in _observations) if (now - pair.Value.Seen > 180) _expiredRecords.Add(pair.Key);
        foreach (var id in _expiredRecords) _observations.Remove(id);
        _expiredRecords.Clear();
        foreach (var pair in _threats) if (now - pair.Value.Seen > 180) _expiredRecords.Add(pair.Key);
        foreach (var id in _expiredRecords) _threats.Remove(id);
        if (_scope.Refresh(_settings.ScanRangeMeters.Value))
        {
            bool omitted = false;
            foreach (var record in _scope.Records)
            {
                if (_rules.Monster(record.GetPrefab()) && !record.GetBool(ZDOVars.s_tamed) && record.GetFloat(ZDOVars.s_health, 0) > 0)
                { if (_threats.Count < 256 || _threats.ContainsKey(record.m_uid)) _threats[record.m_uid] = (record, now); else _threatUnknownUntil = now + 180; }
                if (!_scope.InRange(record.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
                if (!_rules.Eligible(record, out float health, out float maximum)) continue;
                // Healthy buildings must not occupy the damaged-candidate budget.
                if (health >= maximum) { _observations.Remove(record.m_uid); continue; }
                if (!_observations.TryGetValue(record.m_uid, out var observation))
                { if (_observations.Count >= 4096) { omitted = true; continue; } _observations[record.m_uid] = observation = new RepairObservation { Record = record, Identity = record.m_uid, Health = health, Damaged = now }; }
                if (!ReferenceEquals(observation.Record, record)) { _observations.Remove(record.m_uid); continue; }
                if (health < observation.Health) observation.Damaged = now;
                observation.Health = health; observation.Maximum = maximum; observation.Position = record.GetPosition(); observation.Seen = now;
            }
            if (omitted != _observationCapacity)
            {
                _observationCapacity = omitted;
                DiagnosticLog.Write(_log, "HeimMender", omitted ? "Damaged-piece observation capacity reached; additional damaged pieces are deferred." : "Damaged-piece observation capacity is available again.", level: omitted ? LogLevel.Warning : LogLevel.Info);
            }
        }
        if (!_settings.AutomaticEnabled.Value) _cycle.Postpone(now, _settings.IntervalSeconds.Value);
        else if (_cycle.Ready(now) && _kind.Length == 0)
        { _cycle.Postpone(now, _settings.IntervalSeconds.Value); SelectAutomatic(now); }
        if (_kind == "automatic" && !_settings.AutomaticEnabled.Value) _queue.Clear();
        Issue(now);
        Finish(now);
    }
    private bool Nearby(Vector3 point)
    {
        for (int rosterIndex2 = 0; rosterIndex2 < _players.Presence.Peers.Count && _players.Presence.Peers[rosterIndex2] is var p; rosterIndex2++)
            if (p.IsReady() && !p.m_characterID.IsNone() && ZDOMan.instance.GetZDO(p.m_characterID) is ZDO actor &&
                actor.GetOwner() == p.m_uid && actor.GetFloat(ZDOVars.s_health, 0) > 0 &&
                (actor.GetPosition() - point).sqrMagnitude <= _settings.ScanRangeMeters.Value * _settings.ScanRangeMeters.Value) return true;
        return false;
    }
    private bool Threat(Vector3 point, float now)
    {
        float radius = _settings.HostileClearanceMeters.Value;
        if (radius <= 0) return false;
        if (now < _threatUnknownUntil) return true;
        foreach (var pair in _threats)
            if (pair.Key == pair.Value.Record.m_uid && ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), pair.Value.Record) &&
                pair.Value.Record.GetFloat(ZDOVars.s_health, 0) > 0 && !pair.Value.Record.GetBool(ZDOVars.s_tamed) && (pair.Value.Record.GetPosition() - point).sqrMagnitude <= radius * radius) return true;
        return false;
    }
    private void SelectAutomatic(float now)
    {
        int damaged = 0, quiet = 0, threats = 0, owners = 0;
        _playerPositions.Clear(); _threatPositions.Clear();
        for (int rosterIndex3 = 0; rosterIndex3 < _players.Presence.Peers.Count && _players.Presence.Peers[rosterIndex3] is var peer; rosterIndex3++)
            if (peer.IsReady() && !peer.m_characterID.IsNone() && ZDOMan.instance.GetZDO(peer.m_characterID) is ZDO actor &&
                actor.GetOwner() == peer.m_uid && actor.GetFloat(ZDOVars.s_health, 0) > 0) _playerPositions.Add(actor.GetPosition());
        foreach (var pair in _threats)
            if (pair.Key == pair.Value.Record.m_uid && ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), pair.Value.Record) &&
                pair.Value.Record.GetFloat(ZDOVars.s_health, 0) > 0 && !pair.Value.Record.GetBool(ZDOVars.s_tamed)) _threatPositions.Add(pair.Value.Record.GetPosition());
        var candidates = _candidates; candidates.Clear();
        foreach (var candidate in _observations.Values) candidates.Add(candidate);
        for (int i = candidates.Count - 1; i > 0; i--) { int j = _random.Next(i + 1); var temp = candidates[i]; candidates[i] = candidates[j]; candidates[j] = temp; }
        foreach (var candidate in candidates)
        {
            if (candidate.Record.m_uid != candidate.Identity) continue;
            var record = ZDOMan.instance.GetZDO(candidate.Identity);
            if (!ReferenceEquals(record, candidate.Record) || !_rules.Eligible(record!, out float health, out float maximum) || health >= maximum || !NearAny(_playerPositions, record!.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            damaged++;
            if (now - candidate.Damaged < _settings.DamageQuietSeconds.Value) { quiet++; continue; }
            if (_settings.HostileClearanceMeters.Value > 0 && (now < _threatUnknownUntil || NearAny(_threatPositions, record.GetPosition(), _settings.HostileClearanceMeters.Value))) { threats++; continue; }
            if (!HeimMenderRules.OwnerReady(record)) { owners++; continue; }
            if (_random.NextDouble() * 100 >= _settings.RepairChancePercent.Value) continue;
            if (_queue.Count < _settings.MaxRepairsPerCycle.Value) _queue.Enqueue(record.m_uid);
        }
        _diagnostics.Cycle(now, "automatic", candidates.Count, damaged, quiet, threats, owners, _queue.Count);
        if (_queue.Count > 0) _kind = "automatic";
    }
    private static bool NearAny(List<Vector3> positions, Vector3 point, float radius)
    {
        foreach (var position in positions) if ((position - point).sqrMagnitude <= radius * radius) return true;
        return false;
    }
    public bool Request(string target, float? radius, float now)
    {
        if (_kind.Length > 0) return Reject("A repair batch is already running.");
        float range = radius ?? _settings.AdminRadiusMeters.Value;
        if (!PlayerWorldScope.Finite(range) || range < 1 || range > 128) return Reject("Repair radius must be between 1 and 128 metres.");
        var peers = _players.Presence.Peers.Where(p => p.IsReady() && !p.m_characterID.IsNone() &&
            (target.Length == 0 || p.m_playerName == target || p.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture) == target)).Take(2).ToArray();
        if (peers.Length != 1) return Reject("Repair target must identify exactly one ready joined player.");
        var peer = peers[0]; var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (actor == null || actor.GetOwner() != peer.m_uid || actor.GetFloat(ZDOVars.s_health, 0) <= 0 || !PlayerWorldScope.Finite(actor.GetPosition())) return Reject("Repair target position is unavailable.");
        var records = new List<ZDO>();
        var center = actor.GetPosition();
        WorldObjectProvider.Current.For("HeimMender").Query(ZoneSystem.GetZone(center), new SimulationDistance((int)Math.Ceiling(range / 64) + 1, 0), records);
        if (records.Count > 8192) return Reject("Repair scope exceeds the object budget; reduce radius.");
        foreach (var record in records)
        {
            if ((record.GetPosition() - center).sqrMagnitude > range * range || !_rules.Eligible(record, out float health, out float maximum) || health >= maximum || !HeimMenderRules.OwnerReady(record)) continue;
            _queue.Enqueue(record.m_uid);
            if (_queue.Count >= _settings.AdminMaxPieces.Value) break;
        }
        DiagnosticLog.Write(_log, "HeimMender", "Accepted nearby repair request for " + peer.m_playerName + ": " + _queue.Count + " candidates.");
        _diagnostics.Cycle(now, "administrative", records.Count, _queue.Count, 0, 0, 0, _queue.Count);
        if (_queue.Count > 0) _kind = "administrative";
        return true;
    }
    private bool Reject(string message) { DiagnosticLog.Write(_log, "HeimMender", message, level: LogLevel.Warning); return false; }
    private void Issue(float now)
    {
        for (int i = 0; i < 4 && _queue.Count > 0; i++)
        {
            var id = _queue.Dequeue(); var record = ZDOMan.instance.GetZDO(id);
            if (record == null || _pending.ContainsKey(id) || !_rules.Eligible(record, out float health, out float maximum) || health >= maximum || !HeimMenderRules.OwnerReady(record)) continue;
            if (_kind == "automatic" && (!_observations.TryGetValue(id, out var observation) || now - observation.Damaged < _settings.DamageQuietSeconds.Value || Threat(record.GetPosition(), now) || !Nearby(record.GetPosition()))) continue;
            _pending.Add(id, new RepairRequest { Record = record, Sent = now, Maximum = maximum });
            _sent++; _diagnostics.Request(now, record, health, maximum);
            ZRoutedRpc.instance.InvokeRoutedRPC(record.GetOwner(), id, "RPC_Repair");
        }
    }
    private void Reconcile(float now)
    {
        _expiredRecords.Clear();
        foreach (var pair in _pending)
        {
            var request = pair.Value; var record = ZDOMan.instance.GetZDO(pair.Key);
            bool same = ReferenceEquals(record, request.Record);
            bool repaired = same && record!.GetFloat(ZDOVars.s_health, 0) >= request.Maximum;
            if (!repaired && same && now - request.Sent < 10) continue;
            if (repaired) { _confirmed++; _observations.Remove(pair.Key); } else _unconfirmed++;
            _expiredRecords.Add(pair.Key);
        }
        foreach (var id in _expiredRecords) _pending.Remove(id);
    }
    private void Finish(float now)
    {
        if (_kind.Length == 0 || _queue.Count != 0 || _pending.Count != 0) return;
        _diagnostics.Finished(now, _kind, _sent, _confirmed, _unconfirmed);
        _kind = ""; _sent = _confirmed = _unconfirmed = 0;
    }
    private void Observe(ZRpc rpc, ZPackage packet)
    {
        try { ObserveCommand(rpc, packet); }
        catch (System.IO.IOException) { /* Malformed native traffic is not an administrative request. */ }
        catch (ArgumentException) { /* Invalid packet fields retain native handling. */ }
    }
    private void ObserveCommand(ZRpc rpc, ZPackage packet)
    {
        if (packet.Size() > 4096) return;
        var peer = PlayerPeerLookup.Ready(_players.Presence.Peers, rpc);
        if (peer == null) return;
        if (!RoutedActivityReader.TryRead(packet, peer.m_uid, out _, out int method) || method != SayMethod && method != ChatMethod) return;
        var data = ZdoPackets.ReadRouted(packet);
        if (data.m_senderPeerID != peer.m_uid || data.m_targetPeerID != 0 && data.m_targetPeerID != ZDOMan.GetSessionID()) return;
        bool chat = data.m_methodHash == "ChatMessage".GetStableHashCode() && data.m_targetZDO.IsNone();
        bool say = data.m_methodHash == "Say".GetStableHashCode() && data.m_targetZDO == peer.m_characterID;
        if (!chat && !say) return;
        var parameters = data.m_parameters;
        if (chat) parameters.ReadVector3();
        int type = parameters.ReadInt(); parameters.ReadString(); parameters.ReadString();
        string text = parameters.ReadString().Trim();
        if (type < 0 || type > 2 || parameters.GetPos() != parameters.Size() || text != "!heimmender repair") return;
        float now = Time.unscaledTime;
        if (_adminRate.TryGetValue(peer.m_uid, out float last) && now - last < 5) return;
        if (_adminRate.Count >= 64 && !_adminRate.ContainsKey(peer.m_uid)) return;
        _adminRate[peer.m_uid] = now;
        if (!ZNet.instance.IsAdmin(peer.m_socket.GetHostName())) { Reject("Rejected repair request from non-admin " + peer.m_playerName + "."); return; }
        Request(peer.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture), null, now);
    }
    public void Dispose() { _scope.Dispose(); _messages.Dispose(); _queue.Clear(); _pending.Clear(); _observations.Clear(); _threats.Clear(); _adminRate.Clear(); }
}
