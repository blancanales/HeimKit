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
    private PlayerWorldScope _scope;
    private float _healthScale, _now;
    private readonly AdaptiveOperation _cells, _observeWork, _selectionWork, _adminWork, _issueWork;
    private readonly List<ZDOID> _ids = new(), _adminIds = new(), _prune = new();
    private readonly List<ZDO> _liveThreats = new();
    private readonly HashSet<ZDOID> _adminSeen = new();
    private IEnumerator<bool>? _scan, _observe, _selection, _admin;
    private Vector3 _adminCenter;
    private float _adminRange;
    private WorldActor _adminTarget;
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
    private readonly List<WorldActor> _worldActors = new();
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
        _cells = new(log, settings.DebugLogging, "HeimMender", "discovery_cells", 32);
        _observeWork = new(log, settings.DebugLogging, "HeimMender", "damage_observation", 128);
        _selectionWork = new(log, settings.DebugLogging, "HeimMender", "repair_selection", 128);
        _adminWork = new(log, settings.DebugLogging, "HeimMender", "admin_candidates", 64);
        _issueWork = new(log, settings.DebugLogging, "HeimMender", "repair_issue", 4);
        _messages = players.RoutedMessages.Subscribe(Observe, fail);
        _healthScale = _rules.HealthScale();
        try { _scope.Index(Tracked); } catch { _messages.Dispose(); throw; }
    }
    // Selection membership is damage, not eligibility: healthy structures never enter a pass.
    // Core reclassifies a record whenever its replicated or local data changes.
    private bool Tracked(ZDO record) => _rules.Monster(record.GetPrefab()) || _rules.Damaged(record);
    private void Reindex()
    {
        var previous = _scope; _scope = WorldObjectProvider.Current.CreateScope("HeimMender");
        try { _scope.Index(Tracked); } finally { previous.Dispose(); }
    }
    // A restored, replaced or no-longer-eligible piece leaves the selection without another change
    // to observe, so its slot is released here rather than by a later scope pass.
    private bool Settled(RepairObservation observation) =>
        !ReferenceEquals(ZDOMan.instance.GetZDO(observation.Identity), observation.Record) || !_rules.Damaged(observation.Record);
    public void Tick(float now)
    {
        _now = now;
        bool service = ModuleScheduler.Current.Due(this, "processing", now, 2);
        if (service)
        {
            if (_interval != _settings.IntervalSeconds.Value) { _interval = _settings.IntervalSeconds.Value; _cycle.Postpone(now, _interval); }
            // Maximum health follows native world-level scaling; a changed scale alters which stored values count as damage.
            float scale = _rules.HealthScale();
            if (scale != _healthScale) { _healthScale = scale; CancelAutomaticScan(); Reindex(); }
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
            foreach (var pair in _threats) if (now - pair.Value.Seen > 180) _expiredRecords.Add(pair.Key);
            foreach (var id in _expiredRecords) _threats.Remove(id);
        }
        if (!WorldObjectProvider.Current.Ready)
        {
            CancelAutomaticScan();
            if (_admin != null) { _admin.Dispose(); _admin = null; _queue.Clear(); }
            Finish(now);
            return;
        }
        if (_kind == "administrative")
        {
            if (_admin != null)
            {
                if (!_adminWork.RequestedDue(this, now)) return;
                using var batch = _adminWork.Begin(now);
                if (WorkSlice.Advance(_admin, adaptive: batch))
                { _admin.Dispose(); _admin = null; Finish(now); }
            }
            else if (service) { Issue(now); Finish(now); }
            return;
        }
        if (_kind == "automatic" && !_settings.AutomaticEnabled.Value) _queue.Clear();
        if (_kind == "automatic")
        {
            if (_queue.Count > 0 && _issueWork.Due(this, now, 2))
            {
                using var batch = _issueWork.Begin(now);
                Issue(now, batch);
            }
            Finish(now);
            return;
        }
        if (_selection != null)
        {
            if (!_selectionWork.Due(this, now, 0)) return;
            using var batch = _selectionWork.Begin(now);
            if (WorkSlice.Advance(_selection, adaptive: batch)) { _selection.Dispose(); _selection = null; }
            return;
        }
        if (_observe == null && _scan == null)
        {
            if (!ModuleScheduler.Current.Due(this, "background", now, 2, cooperative: true)) return;
            _ids.Clear();
            if (!_scope.BeginCandidateScan(_settings.ScanRangeMeters.Value, _ids, out _scan, outdoorOnly: false, includeDistant: true)) return;
        }
        if (_scan != null)
        {
            if (!_cells.Due(this, now, 0)) return;
            using var batch = _cells.Begin(now);
            bool complete = WorkSlice.Advance(_scan, adaptive: batch);
            if (_ids.Count > 8192) { CancelAutomaticScan(); return; }
            if (!complete) return;
            _scan.Dispose(); _scan = null;
            _observe = ObserveDamage().GetEnumerator();
            return;
        }
        if (!_observeWork.Due(this, now, 0)) return;
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _observations.Count) : null;
        using var observation = _observeWork.Begin(now);
        if (!WorkSlice.Advance(_observe!, adaptive: observation)) return;
        _observe!.Dispose(); _observe = null;
        ModuleScheduler.Current.Postpone(this, "background", now, 2);
        if (!_settings.AutomaticEnabled.Value) _cycle.Postpone(now, _settings.IntervalSeconds.Value);
        else if (_cycle.Ready(now))
        {
            _cycle.Postpone(now, _settings.IntervalSeconds.Value);
            _selection = SelectAutomatic().GetEnumerator();
        }
    }

    private IEnumerable<bool> ObserveDamage()
    {
        _prune.Clear(); _prune.AddRange(_observations.Keys);
        foreach (var id in _prune)
        {
            yield return true;
            if (_observations.TryGetValue(id, out var existing) && (_now - existing.Seen > 180 || Settled(existing)))
                _observations.Remove(id);
        }
        float now = _now;
        bool omitted = false, discovered = false;
        foreach (var id in _ids)
        {
            yield return true;
            now = _now;
            var record = ZDOMan.instance.GetZDO(id);
            if (record == null || !record.IsValid() || record.m_uid != id) continue;
            if (_rules.Monster(record.GetPrefab()) && !record.GetBool(ZDOVars.s_tamed) && record.GetFloat(ZDOVars.s_health, 0) > 0)
            { if (_threats.Count < 256 || _threats.ContainsKey(record.m_uid)) _threats[record.m_uid] = (record, now); else _threatUnknownUntil = now + 180; }
            if (!_scope.InRange(record.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            if (!_rules.Eligible(record, out float health, out float maximum)) continue;
            // Healthy buildings must not occupy the damaged-candidate budget.
            if (health >= maximum) { _observations.Remove(record.m_uid); continue; }
            if (!_observations.TryGetValue(record.m_uid, out var observation))
            { if (_observations.Count >= 4096) { omitted = true; continue; } _observations[record.m_uid] = observation = new RepairObservation { Record = record, Identity = record.m_uid, Health = health, Damaged = now, Share = (float)_random.NextDouble() }; discovered = true; }
            if (!ReferenceEquals(observation.Record, record)) { _observations.Remove(record.m_uid); continue; }
            if (health < observation.Health) observation.Damaged = now;
            observation.Health = health; observation.Maximum = maximum; observation.Position = record.GetPosition(); observation.Seen = now;
        }
        // Newly observed damage must not wait out an idle interval that was set while nothing was damaged.
        if (discovered && _settings.CatchUpIntervalSeconds.Value > 0 && _settings.CatchUpIntervalSeconds.Value < _settings.IntervalSeconds.Value)
            _cycle.Hasten(now, _settings.CatchUpIntervalSeconds.Value);
        if (omitted != _observationCapacity)
        {
            _observationCapacity = omitted;
            DiagnosticLog.Write(_log, "HeimMender", omitted ? "Damaged-piece observation capacity reached; additional damaged pieces are deferred." : "Damaged-piece observation capacity is available again.", level: omitted ? LogLevel.Warning : LogLevel.Info);
        }
    }
    private bool Nearby(Vector3 point)
    {
        WorldActors.Read(_players.Presence, _worldActors);
        foreach (var identity in _worldActors)
            if (WorldActors.Resolve(identity) is ZDO actor && actor.GetFloat(ZDOVars.s_health, 0) > 0 &&
                (actor.GetPosition() - point).sqrMagnitude <= _settings.ScanRangeMeters.Value * _settings.ScanRangeMeters.Value) return true;
        return false;
    }
    private bool Threat(Vector3 point, float now)
    {
        float radius = _settings.HostileClearanceMeters.Value;
        if (radius <= 0) return false;
        if (now < _threatUnknownUntil) return true;
        using var timing = PerformanceStages.Measure("HeimMender", "live_threat_check", 1);
        if (!_scope.ReadCandidatesAt(point, radius, _liveThreats)) return true;
        foreach (var record in _liveThreats)
            if (record.IsValid() && _rules.Monster(record.GetPrefab()) && !record.GetBool(ZDOVars.s_tamed) &&
                record.GetFloat(ZDOVars.s_health, 0) > 0 && (record.GetPosition() - point).sqrMagnitude <= radius * radius) return true;
        foreach (var pair in _threats)
            if (pair.Key == pair.Value.Record.m_uid && ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), pair.Value.Record) &&
                pair.Value.Record.GetFloat(ZDOVars.s_health, 0) > 0 && !pair.Value.Record.GetBool(ZDOVars.s_tamed) && (pair.Value.Record.GetPosition() - point).sqrMagnitude <= radius * radius) return true;
        return false;
    }
    private IEnumerable<bool> SelectAutomatic()
    {
        float now = _now;
        int damaged = 0, quiet = 0, threats = 0, owners = 0, unselected = 0, scheduled = 0;
        _playerPositions.Clear(); _threatPositions.Clear();
        WorldActors.Read(_players.Presence, _worldActors);
        foreach (var identity in _worldActors)
            if (WorldActors.Resolve(identity) is ZDO actor && actor.GetFloat(ZDOVars.s_health, 0) > 0)
                _playerPositions.Add(actor.GetPosition());
        foreach (var pair in _threats)
            if (pair.Key == pair.Value.Record.m_uid && ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), pair.Value.Record) &&
                pair.Value.Record.GetFloat(ZDOVars.s_health, 0) > 0 && !pair.Value.Record.GetBool(ZDOVars.s_tamed)) _threatPositions.Add(pair.Value.Record.GetPosition());
        var candidates = _candidates; candidates.Clear();
        candidates.AddRange(_observations.Values);
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            yield return true;
            now = _now;
            int selected = _random.Next(i + 1);
            var candidate = candidates[selected];
            candidates[selected] = candidates[i];
            if (candidate.Record.m_uid != candidate.Identity) continue;
            var record = ZDOMan.instance.GetZDO(candidate.Identity);
            if (!ReferenceEquals(record, candidate.Record) || !_rules.Eligible(record!, out float health, out float maximum) || health >= maximum || !NearAny(_playerPositions, record!.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            damaged++;
            if (now - candidate.Damaged < _settings.DamageQuietSeconds.Value) { quiet++; continue; }
            // Restoration is gradual: each piece becomes due at its own point in the spread period.
            if (now - candidate.Damaged < candidate.Share * _settings.RepairSpreadSeconds.Value) { scheduled++; continue; }
            if (_settings.HostileClearanceMeters.Value > 0 && (now < _threatUnknownUntil || NearAny(_threatPositions, record.GetPosition(), _settings.HostileClearanceMeters.Value))) { threats++; continue; }
            if (!HeimMenderRules.OwnerReady(record)) { owners++; continue; }
            if (_random.NextDouble() * 100 >= _settings.RepairChancePercent.Value) { unselected++; continue; }
            if (_queue.Count < _settings.MaxRepairsPerCycle.Value) _queue.Enqueue(record.m_uid); else unselected++;
        }
        _diagnostics.Cycle(now, "automatic", candidates.Count, damaged, quiet, threats, owners, _queue.Count, scheduled);
        // Work that this cycle deferred or left unselected returns after the shorter catch-up delay. Each cycle
        // keeps its own request cap and every live check; a zero chance can never select, so it does not hurry.
        float catchUp = _settings.CatchUpIntervalSeconds.Value;
        if (_settings.RepairChancePercent.Value <= 0) unselected = 0;
        if (quiet + threats + scheduled + unselected > 0 && catchUp > 0 && catchUp < _settings.IntervalSeconds.Value) _cycle.Postpone(now, catchUp);
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
        WorldActor? match = null;
        string? matchName = null;
        if (WorldActors.TryLocal(out var host))
        {
            string name = Player.m_localPlayer.GetPlayerName();
            if (Matches(host.Owner, name)) { match = host; matchName = name; }
        }
        foreach (var peer in _players.Presence.Peers)
        {
            if (!peer.IsReady() || peer.m_characterID.IsNone() || !Matches(peer.m_uid, peer.m_playerName)) continue;
            if (match.HasValue) return Reject("Repair target must identify exactly one ready joined player.");
            match = new WorldActor(peer.m_uid, peer.m_characterID);
            matchName = peer.m_playerName;
        }
        if (!match.HasValue) return Reject("Repair target must identify exactly one ready joined player.");
        var actor = WorldActors.Resolve(match.Value);
        if (actor == null || actor.GetFloat(ZDOVars.s_health, 0) <= 0 || !PlayerWorldScope.Finite(actor.GetPosition()))
            return Reject("Repair target position is unavailable.");

        bool Matches(long owner, string name) => target.Length == 0 || name == target ||
            owner.ToString(System.Globalization.CultureInfo.InvariantCulture) == target;
        CancelAutomaticScan();
        _adminCenter = actor.GetPosition(); _adminRange = range; _adminTarget = match.Value;
        _kind = "administrative";
        _admin = CollectAdministrative().GetEnumerator();
        DiagnosticLog.Write(_log, "HeimMender", "Accepted nearby repair request for " + matchName + ".");
        return true;
    }

    private IEnumerable<bool> CollectAdministrative()
    {
        _adminIds.Clear(); _adminSeen.Clear();
        var world = WorldObjectProvider.Current;
        var selection = world.For("HeimMender");
        foreach (bool unit in world.CollectIds(selection, _adminCenter, (int)Math.Ceiling(_adminRange / 64) + 1, false, _adminIds, resultLimit: 8193))
            yield return unit;
        if (_adminIds.Count > 8192)
        {
            Reject("Repair scope exceeds the object budget; reduce radius.");
            yield break;
        }
        if (!world.Ready || !WorldActors.IsCurrent(_players.Presence, _adminTarget)) yield break;
        foreach (var id in _adminIds)
        {
            yield return true;
            var record = ZDOMan.instance.GetZDO(id);
            if (!_adminSeen.Add(id) || record == null || record.m_uid != id || (record.GetPosition() - _adminCenter).sqrMagnitude > _adminRange * _adminRange ||
                !_rules.Eligible(record, out float health, out float maximum) || health >= maximum || !HeimMenderRules.OwnerReady(record)) continue;
            _queue.Enqueue(id);
            if (_queue.Count >= _settings.AdminMaxPieces.Value) break;
        }
        _diagnostics.Cycle(_now, "administrative", _adminIds.Count, _queue.Count, 0, 0, 0, _queue.Count);
    }

    private void CancelAutomaticScan()
    {
        _scan?.Dispose(); _scan = null;
        _observe?.Dispose(); _observe = null;
        if (_selection != null) { _selection.Dispose(); _selection = null; _queue.Clear(); }
    }
    private bool Reject(string message) { DiagnosticLog.Write(_log, "HeimMender", message, level: LogLevel.Warning); return false; }
    private void Issue(float now, AdaptiveOperation? batch = null)
    {
        for (int i = 0; i < 4 && _queue.Count > 0 && (batch?.Take() ?? true); i++)
        {
            var id = _queue.Dequeue(); var record = ZDOMan.instance.GetZDO(id);
            if (record == null || _pending.ContainsKey(id) || !_rules.Eligible(record, out float health, out float maximum) || health >= maximum || !HeimMenderRules.OwnerReady(record)) continue;
            if (_kind == "administrative" && (!WorldActors.IsCurrent(_players.Presence, _adminTarget) ||
                (record.GetPosition() - _adminCenter).sqrMagnitude > _adminRange * _adminRange)) continue;
            if (_kind == "automatic" && _observations.TryGetValue(id, out var last) && health < last.Health)
            { last.Damaged = now; last.Health = health; continue; }
            if (_kind == "automatic" && (!_observations.TryGetValue(id, out var observation) || !ReferenceEquals(record, observation.Record) || now - observation.Damaged < _settings.DamageQuietSeconds.Value || now - observation.Damaged < observation.Share * _settings.RepairSpreadSeconds.Value || Threat(record.GetPosition(), now) || !Nearby(record.GetPosition()))) continue;
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
    public void Dispose() { CancelAutomaticScan(); _admin?.Dispose(); _scope.Dispose(); _messages.Dispose(); _queue.Clear(); _pending.Clear(); _observations.Clear(); _threats.Clear(); _adminRate.Clear(); }
}
