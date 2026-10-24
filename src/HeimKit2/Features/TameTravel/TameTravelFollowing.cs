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
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Features.TameTravel.Models;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelFollowing : IDisposable
{
    private static TameTravelFollowing? _current;
    private static readonly int LoxPrefab = "Lox".GetStableHashCode();
    private readonly Harmony _hooks = new("heimkit.tametravel.following");
    private readonly TameTravelFollowContext _context;
    private readonly TameTravelRules _rules;
    private readonly TameTravelSettings _settings;
    private readonly ObjectTransferAuthority _authority;
    private readonly TameTravelDiagnostics _diagnostics;
    private readonly ManualLogSource _log;
    private readonly Action<Exception> _fail;
    private readonly Dictionary<ZDOID, FollowingSession> _sessions = new();
    private readonly List<KeyValuePair<ZDOID, FollowingSession>> _snapshot = new();
    private readonly Func<BaseAI, float, Vector3, float, bool, bool> _moveTo;
    private int _cursor;
    private bool _disposed;
    public int Count => _sessions.Count;
    public bool ChangingOwners(ZDOID id) => _sessions.TryGetValue(id, out var s) && s.Acquired && s.Lease.Phase != 5;
    internal static MethodInfo NativeMethod(string name, params Type[] parameters)
        => typeof(BaseAI).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null)
            ?? throw new MissingMethodException(typeof(BaseAI).Name, name);

    public TameTravelFollowing(TameTravelRules rules, TameTravelSettings settings, ObjectTransferAuthority authority,
        TameTravelDiagnostics diagnostics, ManualLogSource log, Action<Exception> fail)
    {
        _rules = rules; _settings = settings; _authority = authority; _diagnostics = diagnostics; _log = log; _fail = fail;
        _moveTo = NativeAccess.Method<Func<BaseAI, float, Vector3, float, bool, bool>>(typeof(BaseAI), "MoveTo", typeof(float), typeof(Vector3), typeof(float), typeof(bool));
        _context = new();
        try
        {
            if (_current != null) throw new InvalidOperationException("TameTravel following is already active.");
            _current = this;
            if (settings.FollowSpacingEnabled.Value)
                _hooks.Patch(NativeMethod("Follow", typeof(GameObject), typeof(float)), prefix: new HarmonyMethod(typeof(TameTravelFollowing), nameof(Follow)));
        }
        catch { if (ReferenceEquals(_current, this)) _current = null; _hooks.UnpatchSelf(); _context.Dispose(); throw; }
    }
    public void Command(ZNetPeer peer, ZDO animal, TameOrder order)
    {
        if (order != TameOrder.Follow) { Stop(animal.m_uid, "stay_or_wait"); return; }
        if (!_settings.FollowSpacingEnabled.Value) return;
        if (_sessions.TryGetValue(animal.m_uid, out var previous))
        {
            if (ReferenceEquals(previous.Lease.Journey.Peer, peer) && previous.Lease.Journey.Character == peer.m_characterID) return;
            Stop(animal.m_uid, "new_player_session");
        }
        if (_sessions.Count >= _settings.MaxControlledFollowers.Value)
        {
            DiagnosticLog.Write(_log, "TameTravel", $"Follow spacing unavailable for {peer.m_playerName}: controlled-follower capacity reached; native following remains.", level: LogLevel.Warning);
            Feedback(peer, "Follow spacing capacity reached; native following remains. Try /comehere again later."); return;
        }
        float now = Time.unscaledTime;
        var lease = new AnimalTransfer { Id = animal.m_uid, Record = animal, FollowControl = true, Started = now, PhaseTime = now,
            Journey = new PortalJourney { Peer = peer, Character = peer.m_characterID, Name = peer.m_playerName } };
        _sessions.Add(animal.m_uid, new FollowingSession { Lease = lease, Requested = now });
        _diagnostics.Following(lease, "requested", 0, 0, 0);
    }
    private bool Valid(FollowingSession s, out ZDO actor)
        => InvalidReason(s, out actor) == null;
    private string? InvalidReason(FollowingSession s, out ZDO actor)
    {
        var t = s.Lease;
        string? reason = _rules.ActorRejection(t.Journey.Peer, out actor);
        if (reason != null) return reason;
        if (t.Journey.Character != t.Journey.Peer.m_characterID) return "character_replaced";
        if (t.Journey.Name != t.Journey.Peer.m_playerName) return "player_name_changed";
        if (!ReferenceEquals(ZDOMan.instance.GetZDO(t.Id), t.Record) || t.Record.m_uid != t.Id) return "animal_record_replaced";
        reason = _rules.AnimalRejection(t.Record); if (reason != null) return reason;
        if (t.Record.GetString(ZDOVars.s_follow) != t.Journey.Name) return "follow_state_changed";
        return (t.Record.GetPosition() - actor.GetPosition()).sqrMagnitude > 32 * 32 ? "follower_range_exceeded" : null;
    }
    public void Tick(float now)
    {
        Snapshot();
        foreach (var pair in _snapshot)
        {
            var s = pair.Value;
            string? reason = InvalidReason(s, out _);
            if (reason == null && s.Acquired && !_authority.Holds(s.Lease)) reason = "follower_ownership_changed";
            if (reason != null) Stop(pair.Key, reason);
        }
        Snapshot();
        var sessions = _snapshot;
        if (sessions.Count == 0) return;
        // Rotate one potentially expensive native context query per controller pass.
        var selected = sessions[_cursor % sessions.Count].Value; _cursor = (_cursor + 1) % sessions.Count;
        if (now >= selected.NextContext && Valid(selected, out var actor))
        {
            if (_context.Prepare(selected.Lease.Record, actor, now))
            {
                selected.ContextAt = now; selected.ContextPosition = selected.Lease.Record.GetPosition(); selected.UnreadySince = -1;
                var capsule = PrefabRegistry.Component<CapsuleCollider>(_rules.Prefab(selected.Lease.Record))!;
                TameTravelRules.Scale(selected.Lease.Record, out var scale);
                float animalExtent = (capsule.radius + (capsule.direction == 1 ? 0 : Math.Max(0, capsule.height * .5f - capsule.radius)) +
                    new Vector2(capsule.center.x, capsule.center.z).magnitude) * scale.x;
                var playerCapsule = ZNetScene.instance.FindInstance(actor)?.GetComponent<CapsuleCollider>();
                if (playerCapsule == null) selected.ContextAt = float.NegativeInfinity;
                else (selected.StopDistance, selected.ResumeDistance) = TameTravelFollowRules.Distances(_settings.FollowStopDistanceMeters.Value,
                    _settings.FollowResumeDistanceMeters.Value, animalExtent, playerCapsule.radius * Math.Abs(playerCapsule.transform.lossyScale.x),
                    lox: selected.Lease.Record.GetPrefab() == LoxPrefab);
            }
            else selected.ContextAt = float.NegativeInfinity;
            selected.NextContext = now + (float.IsNegativeInfinity(selected.ContextAt) ? .25f : 1f);
        }
        foreach (var pair in sessions)
        {
            var s = pair.Value; var t = s.Lease;
            if (s.Acquired && t.Phase == 0 && now - t.PhaseTime > 10) { Stop(pair.Key, "source_drain_timeout", warning: true); continue; }
            bool context = now - s.ContextAt <= 3 && (t.Record.GetPosition() - s.ContextPosition).sqrMagnitude <= 16 * 16;
            if (!context)
            {
                if (s.UnreadySince < 0) s.UnreadySince = now;
                if (s.Acquired && t.Phase == 5) { _authority.PauseFollow(t); _diagnostics.Following(t, "context_paused", 0, s.StopDistance, s.ResumeDistance); }
                if (now - s.UnreadySince > 10) Stop(pair.Key, "context_timeout", warning: true);
                continue;
            }
            if (!s.Acquired)
            {
                // No takeover until the original animal/player instances and collision context exist.
                if (!_authority.Acquire(t, now)) { Stop(pair.Key, "acquisition_unavailable", warning: true); continue; }
                s.Acquired = true; t.PhaseTime = now;
            }
            if (t.Phase == 0)
            {
                if (now - t.PhaseTime > 10 || t.PreviousPeer == null || !t.PreviousPeer.IsReady()) { Stop(pair.Key, "source_drain_timeout", warning: true); continue; }
                if (!_authority.Echoed(t.Barrier)) continue;
                if (!t.SourceStateObserved)
                {
                    if (t.SourceEchoTime == 0) t.SourceEchoTime = now;
                    if (now - t.SourceEchoTime >= 2) _authority.RequestSourceState(t, now);
                    continue;
                }
                t.Phase = 1;
            }
            if (t.Phase == 1 || t.Phase == 6)
            {
                var instance = ZNetScene.instance.FindInstance(t.Record);
                var player = ZNetScene.instance.FindInstance(t.Journey.Character);
                var ai = instance == null ? null : instance.GetComponent<MonsterAI>();
                if (ai == null || player == null) { Stop(pair.Key, "instance_unavailable", warning: true); continue; }
                ai.SetFollowTarget(player); ai.StopMoving();
                _authority.RunFollow(t);
                // Initialize native ownership sync from the drained record before motion resumes.
                instance!.GetComponent<ZSyncTransform>()!.SyncNow();
                if (!s.Started)
                {
                    s.Started = true;
                    DiagnosticLog.Write(_log, "TameTravel", $"Server follow spacing active for {t.Journey.Name}.");
                    Feedback(t.Journey.Peer, "Follow spacing active: stop " + s.StopDistance.ToString("0.0") + "m, resume " + s.ResumeDistance.ToString("0.0") + "m.");
                }
                _diagnostics.Following(t, "simulation_started", 0, s.StopDistance, s.ResumeDistance);
            }
        }
    }
    private static bool Follow(BaseAI __instance, GameObject go, float dt)
    {
        var c = _current;
        if (c == null || c._disposed || c._sessions.Count == 0) return true;
        try { return c.Apply(__instance, go, dt); }
        catch (Exception error) { c._fail(error); return false; }
    }
    private bool Apply(BaseAI ai, GameObject target, float dt)
    {
        var z = ai.GetComponent<ZNetView>()?.GetZDO();
        if (z == null || !_sessions.TryGetValue(z.m_uid, out var s) || !_authority.RunningFollow(z.m_uid)) return true;
        if (target == null || target.GetComponent<ZNetView>()?.GetZDO()?.m_uid != s.Lease.Journey.Character || !Valid(s, out _) ||
            Time.unscaledTime - s.ContextAt > 3 || (ai.transform.position - s.ContextPosition).sqrMagnitude > 16 * 16)
        { ai.StopMoving(); _authority.PauseFollow(s.Lease); return false; }
        long started = _diagnostics.BeginFollowing();
        try
        {
        var delta = target.transform.position - ai.transform.position;
        float distanceSquared = delta.x * delta.x + delta.z * delta.z;
        bool stopped = TameTravelFollowRules.Stopped(s.Stopped, distanceSquared, s.StopDistance, s.ResumeDistance);
        if (stopped != s.Stopped)
        {
            s.Stopped = stopped;
            _diagnostics.Following(s.Lease, stopped ? "stopped" : "resumed", (float)Math.Sqrt(distanceSquared), s.StopDistance, s.ResumeDistance);
        }
        if (stopped) ai.StopMoving();
        else _moveTo(ai, dt, target.transform.position, s.StopDistance, distanceSquared > Math.Max(10, s.ResumeDistance + 3) * Math.Max(10, s.ResumeDistance + 3));
        return false;
        }
        finally { _diagnostics.EndFollowing(started, _sessions.Count); }
    }
    public void Stop(ZDOID id, string reason, bool warning = false)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;
        _sessions.Remove(id);
        _diagnostics.Traversal(s.Lease.Journey.Peer, "follow_spacing_ended", reason, animal: id, transition: true);
        if (s.Acquired && _authority.Holds(s.Lease)) _authority.Release(id);
        if (s.Started || warning)
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Follow, $"Server follow spacing released for {s.Lease.Journey.Name}.", new (string, object?)[] { ("animal", id), ("reason", reason) }, warning ? LogLevel.Warning : LogLevel.Info);
        if (warning) Feedback(s.Lease.Journey.Peer, "Follow spacing unavailable (" + reason + "); native following remains. Try /comehere again later.");
    }
    private void Snapshot()
    {
        _snapshot.Clear();
        foreach (var pair in _sessions) _snapshot.Add(pair);
    }
    private static void Feedback(ZNetPeer peer, string text)
    {
        if (peer.IsReady() && !peer.m_characterID.IsNone())
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "Message", (int)MessageHud.MessageType.TopLeft, "TameTravel: " + text, 0);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        var errors = new List<Exception>();
        foreach (var id in _sessions.Keys.ToArray()) try { Stop(id, "controller_stopped"); } catch (Exception error) { errors.Add(error); }
        foreach (Action step in new Action[] { () => _hooks.UnpatchSelf(), () => _context.Dispose() })
            try { step(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Following cleanup failed.", errors);
    }
}
