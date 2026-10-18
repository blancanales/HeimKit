using HeimKit.Shared.Players.Models;
using HeimKit.Shared;
using HeimKit.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace HeimKit.Shared.Players;

// Shared activity observer. It never modifies native traffic.
public sealed class PlayerActivityService : IPlayerActivityObservations, IDisposable
{
    public sealed class Lease : IDisposable
    {
        private bool _released;
        public PlayerActivityService Service { get; }
        internal Lease(PlayerActivityService service) => Service = service;
        public void Dispose() { if (_released) return; _released = true; Service.Release(this); }
    }
    private static PlayerActivityService? _current;
    private readonly PlayerActivityTracker _tracker;
    private readonly PlayerActivityOptions _options;
    private readonly Harmony _harmony = new("heimkit.player_activity");
    private readonly PlayerRoutedMessages? _routedMessages;
    private IDisposable? _routedSubscription;
    private readonly Dictionary<long, (ZDOID Target, float Requested)> _containers = new();
    private int _craftingKey;
    private readonly Dictionary<Lease, Func<bool>> _consumers = new();
    private readonly IPlayerPresence _presence;
    private readonly Func<bool> _ready;
    private readonly Func<List<PlayerActivityObservation>> _readActors;
    private readonly Action<bool> _observer;
    private readonly PlayerActivityDiagnostics _diagnostics;
    private readonly PlayerActivityNotices _notices;
    public bool Reliable { get; private set; }
    public int Consumers => _consumers.Count;
    public long Samples { get; private set; }
    public bool Available => _ready() && _presence.HasJoinedPlayers == true;
    private float _last = -1;
    private ZDOMan? _world;
    private IReadOnlyList<PlayerActivityView> _views = Array.Empty<PlayerActivityView>();
    private readonly ManualLogSource _logger;
    private readonly string _logName;
    public ObservationCounters Hooks { get; } = new();
    private readonly Dictionary<string, (float At, long Suppressed)> _incidents = new();
    private void Incident(float now, string operation, Exception error)
    {
        string identity = operation + ":" + error.GetType().FullName;
        bool first = !_incidents.TryGetValue(identity, out var prior);
        if (!first && now >= prior.At && now - prior.At < 60) { _incidents[identity] = (prior.At, prior.Suppressed + 1); return; }
        if (_incidents.Count >= 16 && first) _incidents.Clear();
        _incidents[identity] = (now, 0);
        Warn(now, "Activity observation unavailable; native eligibility retained and idle inference reset.");
        DiagnosticLog.Write(_logger, _logName, DiagnosticEvent.Lifecycle, first ? "Activity observation failed" : "Activity observation still unavailable", new (string, object?)[] { ("operation", operation), ("exception_type", error.GetType().FullName), ("repeats_suppressed", prior.Suppressed), ("exception", first ? error.ToString() : null) }, LogLevel.Error);
    }
    private float _lastWarning = float.NegativeInfinity;
    public Lease Acquire(Func<bool>? debug = null)
    {
        if (_consumers.Count == 0)
        {
            try { _observer(true); }
            catch { try { _observer(false); } finally { Reset(); } throw; }
        }
        var lease = new Lease(this);
        _consumers.Add(lease, debug ?? (() => false));
        return lease;
    }
    public PlayerActivityService(PlayerActivityOptions options, ManualLogSource logger, IPlayerPresence presence,
        Func<bool>? ready = null, Func<List<PlayerActivityObservation>>? readActors = null, Action<bool>? observer = null, DebugLoggingPolicy? debugPolicy = null,
        PlayerRoutedMessages? routedMessages = null)
    {
        _logName = "PlayerActivity"; _options = options; _tracker = new(options); _logger = logger;
        _presence = presence; _ready = ready ?? (() => Ready); _readActors = readActors ?? (() => ReadActors(_presence, _craftingKey));
        _notices = new(logger);
        _observer = observer ?? SetObserver;
        _routedMessages = routedMessages;
        _diagnostics = new(logger, () => debugPolicy?.Suppressed != true &&
            (debugPolicy?.Enabled == true || _consumers.Values.Any(enabled => enabled())));
        Hooks.Add("routed_hook_calls", 0); Hooks.Add("routed_not_ready", 0); Hooks.Add("authenticated_activity_signals", 0);
    }
    private void SetObserver(bool install)
    {
        if (_routedMessages != null)
        {
            if (!install)
            {
                try { _routedSubscription?.Dispose(); }
                finally { _routedSubscription = null; if (ReferenceEquals(_current, this)) _current = null; }
                return;
            }
            if (_current != null && !ReferenceEquals(_current, this)) throw new InvalidOperationException("An activity observer is already registered.");
            _craftingKey = 438569 + ZSyncAnimation.GetHash("crafting"); _current = this;
            _routedSubscription = _routedMessages.Subscribe(Observe, error => { Reset("routed observation unavailable"); Incident(Time.unscaledTime, "routed_observation", error); });
            return;
        }
        if (!install) { if (_current != null && !ReferenceEquals(_current, this)) return; try { _harmony.UnpatchSelf(); } finally { if (ReferenceEquals(_current, this)) _current = null; } return; }
        if (_current != null && !ReferenceEquals(_current, this)) throw new InvalidOperationException("An activity observer is already registered.");
        _craftingKey = 438569 + ZSyncAnimation.GetHash("crafting");
        _harmony.Patch(AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"), prefix: new HarmonyMethod(typeof(PlayerActivityService), nameof(Observe)));
        _current = this;
    }
    private void Reset(string reason = "activity tracking stopped")
    {
        Reliable = false; _tracker.Clear(); _containers.Clear(); _views = Array.Empty<PlayerActivityView>(); _last = -1;
        _diagnostics.Reset();
        try { _notices.Reset(reason, _presence.JoinedCount); }
        catch (Exception error) { Incident(0, "diagnostics:afk_notices", error); }
    }
    private void Release(Lease lease)
    {
        if (!_consumers.Remove(lease) || _consumers.Count != 0) return;
        try { _observer(false); } finally { Reset(); _incidents.Clear(); _lastWarning = float.NegativeInfinity; }
    }
    public void Dispose()
    {
        bool observing = _consumers.Count > 0; _consumers.Clear();
        try { if (observing) _observer(false); } finally { Reset(); _incidents.Clear(); _lastWarning = float.NegativeInfinity; }
    }
    public void Tick(float now)
    {
        if (_consumers.Count == 0) return;
        Snapshot(now);
        try { _diagnostics.Players(now, _views); }
        catch (Exception error) { Incident(now, "diagnostics", error); }
    }
    public static bool Ready => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated() && ZDOMan.instance != null && Game.instance != null;
    public IReadOnlyList<PlayerActivityView> Snapshot(float now)
    {
        try { Sample(now); }
        catch (Exception error) { Reset("activity observations unavailable"); _last = now; Incident(now, "sampling", error); }
        return _views;
    }
    private void ReportAFK(float now)
    {
        try { _notices.Observe(_views, _presence.JoinedCount); }
        catch (Exception error) { Incident(now, "diagnostics:afk_notices", error); }
    }
    private IReadOnlyList<PlayerActivityView> Sample(float now)
    {
        if (_consumers.Count == 0 || !Available) { Reset(_presence.HasJoinedPlayers == false ? "server empty" : "player or world observations unavailable"); return _views; }
        if (!ReferenceEquals(_world, ZDOMan.instance)) { Reset("world changed"); _world = ZDOMan.instance; }
        if (_last >= 0 && now >= _last && now - _last < _options.Interval) return _views;
        _last = now;
        var observations = _readActors(); Samples++;
        if (observations.Count > 4096) Warn(now, "Connected-player capacity exceeded; native behavior retained");
        var connected = new HashSet<long>(observations.Select(p => p.Peer));
        foreach (long peer in _containers.Keys.Where(peer => !connected.Contains(peer)).ToArray()) _containers.Remove(peer);
        foreach (var player in observations)
        {
            if (!_containers.TryGetValue(player.Peer, out var container)) continue;
            var data = ZDOMan.instance.GetZDO(container.Target);
            if (data != null && data.GetOwner() == player.Peer && data.GetInt(ZDOVars.s_inUse) != 0) player.Busy = true;
            else if (now - container.Requested < 10 && now >= container.Requested) player.Busy = true;
            else _containers.Remove(player.Peer);
        }
        _views = _tracker.Update(now, observations); Reliable = true;
        var recovered = _incidents.Keys.Where(key => !key.StartsWith("diagnostics:", StringComparison.Ordinal)).ToArray();
        if (recovered.Length > 0)
        {
            foreach (string identity in recovered) _incidents.Remove(identity);
            DiagnosticLog.Write(_logger, _logName, DiagnosticEvent.Lifecycle, "Activity observations recovered; idle grace restarted.");
        }
        ReportAFK(now);
        return _views;
    }
    public static List<PlayerActivityObservation> ReadActors(IPlayerPresence presence, int craftingKey = 0)
    {
        var result = new List<PlayerActivityObservation>();
        if (!Ready) return result;
        foreach (var peer in presence.Peers)
        {
            if (result.Count >= 4096) { result.Add(new PlayerActivityObservation { Peer = long.MinValue }); break; }
            if (!peer.IsReady()) { result.Add(new PlayerActivityObservation { Peer = peer.m_uid, Name = peer.m_playerName ?? "" }); continue; }
            var actor = new PlayerActivityObservation { Peer = peer.m_uid, Name = peer.m_playerName ?? "", Character = peer.m_characterID.ToString() };
            result.Add(actor);
            if (peer.m_characterID == ZDOID.None) continue;
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (zdo == null || !zdo.IsValid() || zdo.GetOwner() != peer.m_uid) continue;
            var position = zdo.GetPosition();
            actor.X = position.x; actor.Y = position.y; actor.Z = position.z;
            actor.Heading = zdo.GetRotation().eulerAngles.y;
            actor.Health = zdo.GetFloat(ZDOVars.s_health, float.NaN);
            actor.Known = PlayerActivityTracker.Finite(actor.Health) && PlayerActivityTracker.Finite(actor.X) && PlayerActivityTracker.Finite(actor.Y) && PlayerActivityTracker.Finite(actor.Z) && PlayerActivityTracker.Finite(actor.Heading);
            actor.Alive = actor.Health > 0 && !zdo.GetBool(ZDOVars.s_dead);
            actor.InBed = zdo.GetBool(ZDOVars.s_inBed);
            actor.Busy = craftingKey != 0 && zdo.GetInt(craftingKey) != 0;
            actor.Equipment = $"{zdo.GetInt(ZDOVars.s_rightItem)}:{zdo.GetInt(ZDOVars.s_leftItem)}:{zdo.GetInt(ZDOVars.s_chestItem)}:{zdo.GetInt(ZDOVars.s_helmetItem)}:{zdo.GetInt(ZDOVars.s_legItem)}:{zdo.GetInt(ZDOVars.s_shoulderItem)}:{zdo.GetInt(ZDOVars.s_utilityItem)}:{zdo.GetInt(ZDOVars.s_rightBackItem)}:{zdo.GetInt(ZDOVars.s_leftBackItem)}";
            actor.Emote = zdo.GetInt(ZDOVars.s_emoteID);
        }
        return result;
    }
    private static void Observe(ZRpc rpc, ZPackage pkg)
    {
        var service = _current;
        if (service == null) return;
        service.Hooks.Hit("routed_hook_calls");
        if (!service.Available) { service.Hooks.Hit("routed_not_ready"); return; }
        try
        {
            var peer = service._presence.Peers.FirstOrDefault(p => p.IsReady() && ReferenceEquals(p.m_rpc, rpc));
            if (peer == null || peer.m_characterID == ZDOID.None) return;
            var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid) return;
            if (!RoutedActivityReader.TryRead(pkg, peer.m_uid, out var target, out int method) || !RoutedActivityReader.IsActivity(method)) return;
            if (method == "SetTrigger".GetStableHashCode() && target != peer.m_characterID) return;
            service.Hooks.Hit("authenticated_activity_signals");
            service._tracker.Signal(peer.m_uid, Time.unscaledTime, RoutedActivityReader.ActivityName(method));
            if (service.Reliable) service.ReportAFK(Time.unscaledTime);
            if (method == "RPC_RequestOpen".GetStableHashCode() && target != ZDOID.None && service._containers.Count < 4096)
                service._containers[peer.m_uid] = (target, Time.unscaledTime);
        }
        catch (Exception error)
        {
            // Discard unreliable inference and restore join grace. Native RPC still runs unchanged.
            service.Reset("activity observations unavailable");
            service.Incident(Time.unscaledTime, "routed_observation", error);
        }
    }
    private void Warn(float now, string message)
    {
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        DiagnosticLog.Write(_logger, _logName, DiagnosticEvent.Lifecycle, message, level: LogLevel.Warning);
    }
}
