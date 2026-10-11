using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace HeimKit.Shared;

// One observer shared by independently disposable consumers. It never modifies native traffic.
public sealed class PlayerActivityService
{
    public sealed class Lease : IDisposable
    {
        private bool _released;
        public PlayerActivityService Service { get; }
        internal Lease(PlayerActivityService service) => Service = service;
        public void Dispose() { if (_released) return; _released = true; Service.Release(); }
    }
    private static PlayerActivityService? _current;
    private readonly PlayerActivityTracker _tracker;
    private readonly PlayerActivityOptions _options;
    private readonly Harmony _harmony = new("heimkit.player_activity");
    private readonly Dictionary<long, (ZDOID Target, float Requested)> _containers = new();
    private int _consumers, _craftingKey;
    private float _last = -1;
    private ZDOMan? _world;
    private IReadOnlyList<PlayerActivityView> _views = Array.Empty<PlayerActivityView>();
    private readonly ManualLogSource _logger;
    public HookCounters Hooks { get; } = new();
    private float _lastWarning = float.NegativeInfinity;
    public static Lease Acquire(PlayerActivityOptions options, ManualLogSource logger)
    {
        if (_current == null)
        {
            var service = new PlayerActivityService(options, logger);
            try
            {
                service._craftingKey = 438569 + ZSyncAnimation.GetHash("crafting");
                service._harmony.Patch(AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"), prefix: new HarmonyMethod(typeof(PlayerActivityService), nameof(Observe)));
                _current = service;
            }
            catch { service._harmony.UnpatchSelf(); throw; }
        }
        DiagnosticLog.Write(logger, "PlayerActivity", DiagnosticEvent.Lifecycle, "Shared observer lease acquired", new (string, object?)[] { ("hook", "ZRoutedRpc.RPC_RoutedRPC"), ("registered", true), ("consumers_after", _current._consumers + 1) });
        _current._consumers++;
        return new Lease(_current);
    }
    private PlayerActivityService(PlayerActivityOptions options, ManualLogSource logger) { _options = options; _tracker = new(options); _logger = logger; Hooks.Add("routed_hook_calls", 0); Hooks.Add("routed_not_ready", 0); Hooks.Add("authenticated_activity_signals", 0); }
    private void Release()
    {
        if (--_consumers != 0) return;
        _harmony.UnpatchSelf(); _tracker.Clear(); _containers.Clear(); _views = Array.Empty<PlayerActivityView>(); _current = null;
    }
    public static bool Ready => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated() && ZDOMan.instance != null && Game.instance != null;
    public IReadOnlyList<PlayerActivityView> Snapshot(float now)
    {
        if (!Ready) { _tracker.Clear(); _containers.Clear(); _last = -1; return _views = Array.Empty<PlayerActivityView>(); }
        if (!ReferenceEquals(_world, ZDOMan.instance)) { _world = ZDOMan.instance; _tracker.Clear(); _containers.Clear(); _last = -1; }
        if (_last >= 0 && now >= _last && now - _last < _options.Interval) return _views;
        _last = now;
        var observations = ReadActors(_craftingKey);
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
        return _views = _tracker.Update(now, observations);
    }
    public static List<PlayerActivityObservation> ReadActors(int craftingKey = 0)
    {
        var result = new List<PlayerActivityObservation>();
        if (!Ready) return result;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (!peer.IsReady()) continue;
            if (result.Count >= 4096) { result.Add(new PlayerActivityObservation { Peer = long.MinValue }); break; }
            var actor = new PlayerActivityObservation { Peer = peer.m_uid, Character = peer.m_characterID.ToString() };
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
        if (!Ready) { service.Hooks.Hit("routed_not_ready"); return; }
        try
        {
            var peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.IsReady() && ReferenceEquals(p.m_rpc, rpc));
            if (peer == null || peer.m_characterID == ZDOID.None) return;
            var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid) return;
            if (!RoutedActivityReader.TryRead(pkg, peer.m_uid, out var target, out int method) || !RoutedActivityReader.IsActivity(method)) return;
            if (method == "SetTrigger".GetStableHashCode() && target != peer.m_characterID) return;
            service.Hooks.Hit("authenticated_activity_signals");
            service._tracker.Signal(peer.m_uid, Time.unscaledTime, RoutedActivityReader.ActivityName(method));
            if (method == "RPC_RequestOpen".GetStableHashCode() && target != ZDOID.None && service._containers.Count < 4096)
                service._containers[peer.m_uid] = (target, Time.unscaledTime);
        }
        catch (Exception error)
        {
            // Discard unreliable inference and restore join grace. Native RPC still runs unchanged.
            service._tracker.Clear(); service._containers.Clear(); service._last = -1;
            service.Warn(Time.unscaledTime, "Activity observation failed; idle inference reset: " + error.Message);
        }
    }
    private void Warn(float now, string message)
    {
        if (now >= _lastWarning && now - _lastWarning < 60) return;
        _lastWarning = now;
        DiagnosticLog.Write(_logger, "PlayerActivity", DiagnosticEvent.Lifecycle, message, level: LogLevel.Warning);
    }
}
