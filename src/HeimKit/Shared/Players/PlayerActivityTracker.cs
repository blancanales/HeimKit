using HeimKit.Shared.Players.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HeimKit.Shared.Players;

public sealed class PlayerActivityTracker
{
    private sealed class Session
    {
        public string Character = "";
        public float Joined, LastActivity, LastSample;
        public PlayerActivityObservation? Anchor;
        public PlayerActivityObservation? Previous;
        public PlayerActivityView View = new();
        public bool WasReliable;
    }
    private readonly Dictionary<long, Session> _sessions = new();
    private readonly PlayerActivityOptions _options;
    private long _sessionSequence;
    public PlayerActivityTracker(PlayerActivityOptions options) => _options = options;
    public IReadOnlyList<PlayerActivityView> Update(float now, IEnumerable<PlayerActivityObservation> observations)
    {
        var connected = new HashSet<long>();
        foreach (var current in observations)
        {
            if (!connected.Add(current.Peer)) continue;
            if (!_sessions.TryGetValue(current.Peer, out var state))
            { state = new Session(); state.View.Session = ++_sessionSequence; _sessions.Add(current.Peer, state); }
            bool reset = state.Character != current.Character || state.Previous == null || !state.WasReliable ||
                now < state.LastSample || now - state.LastSample > _options.Interval * 3 + 1;
            if (reset)
            {
                state.View.Recovery = state.Previous == null ? "join" : state.Character != current.Character ? "character_changed" :
                    !state.WasReliable ? "unreliable_actor_recovery" : now < state.LastSample ? "clock_rollback" : "observation_gap";
                state.View.LastSignal = "none_observed";
                state.Joined = state.LastActivity = now; state.Anchor = current;
            }
            string reason = reset ? "join_or_observation_grace" : "no_observed_activity";
            bool known = current.Known && Finite(now) && Finite(current.X) && Finite(current.Y) && Finite(current.Z) && Finite(current.Heading) && Finite(current.Health);
            if (!known || !current.Alive)
            { state.LastActivity = now; reason = !known ? "unknown_actor_data" : "dead_or_loading"; }
            else if (current.InBed || current.Busy)
            { state.LastActivity = now; reason = current.InBed ? "in_bed" : "station_or_container_use"; state.View.LastSignal = reason; }
            else if (!reset && state.Anchor != null && state.Previous != null)
            {
                double dx = current.X - state.Anchor.X, dy = current.Y - state.Anchor.Y, dz = current.Z - state.Anchor.Z;
                double angle = Math.Abs(current.Heading - state.Anchor.Heading) % 360;
                if (angle > 180) angle = 360 - angle;
                if (dx * dx + dy * dy + dz * dz >= _options.Movement * _options.Movement || angle >= _options.Look ||
                    current.Health < state.Previous.Health || current.Equipment != state.Previous.Equipment || current.Emote != state.Previous.Emote || state.Previous.InBed)
                { state.LastActivity = now; state.Anchor = current; reason = "movement_rotation_damage_equipment_or_emote";
                    state.View.LastSignal = current.Health < state.Previous.Health ? "health_decrease" : current.Equipment != state.Previous.Equipment ? "equipment_changed" :
                        current.Emote != state.Previous.Emote ? "emote_changed" : state.Previous.InBed ? "left_bed" : angle >= _options.Look ? "body_rotation" : "movement"; }
            }
            if (current.Busy || current.InBed || !known || !current.Alive) state.Anchor = current;
            state.Character = current.Character; state.LastSample = now; state.Previous = current;
            state.WasReliable = known && current.Alive;
            var view = state.View;
            bool previousKnown = view.Known, previousAlive = view.Alive, previousBed = view.InBed, previousIdle = view.Idle;
            view.Peer = current.Peer; view.Known = known; view.Alive = current.Alive; view.InBed = known && current.Alive && current.InBed;
            view.X = current.X; view.Y = current.Y; view.Z = current.Z;
            view.IdleSeconds = Math.Max(0, now - state.LastActivity);
            view.Idle = known && current.Alive && !current.InBed && !current.Busy && now - state.Joined >= _options.Grace && view.IdleSeconds >= _options.Timeout;
            view.Reason = view.Idle ? "inferred_idle" : reason;
            view.ObservedAt = now; view.LastActivityAt = state.LastActivity;
            view.GraceRemaining = Math.Max(0, _options.Grace - (now - state.Joined));
            if (reset || previousKnown != view.Known || previousAlive != view.Alive || previousBed != view.InBed || previousIdle != view.Idle) view.StateRevision++;
        }
        foreach (long peer in _sessions.Keys.Where(peer => !connected.Contains(peer)).ToArray()) _sessions.Remove(peer);
        return _sessions.Values.Select(state => state.View).ToArray();
    }
    public void Signal(long peer, float now, string signal = "routed_activity")
    {
        if (!_sessions.TryGetValue(peer, out var state) || !Finite(now)) return;
        state.LastActivity = now; state.Anchor = state.Previous;
        if (state.View.Idle) state.View.StateRevision++;
        state.View.ObservedAt = now; state.View.LastSignal = signal; state.View.LastActivityAt = now;
        state.View.Idle = false; state.View.IdleSeconds = 0; state.View.Reason = "observed_routed_activity";
    }
    public void Clear() => _sessions.Clear();
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
