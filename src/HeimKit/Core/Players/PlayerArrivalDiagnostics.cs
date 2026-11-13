using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Core.Players;

// Passive, bounded observation windows. This does not implement arrival scheduling.
internal sealed class PlayerArrivalDiagnostics : IDisposable
{
    private sealed class State
    {
        internal long Session, Seen;
        internal ZDOID Actor;
        internal Vector3 Position;
        internal float At;
        internal bool Positioned;
        internal Window? Active;
    }
    private sealed class Window
    {
        internal long Id, Session, Peer, WorstFrame;
        internal ZDOID Actor;
        internal string Name = "", Kind = "", End = "";
        internal Vector3? From;
        internal Vector3 To;
        internal float Started, Gap, Closed;
        internal float FirstZoneLoaded = float.NegativeInfinity, FirstCached = float.NegativeInfinity;
        internal bool Interim, DestinationObserved = true;
        internal int PinComparisonsSkipped;
        internal int Frames, WorstGc, Concurrent;
        internal double Total, Maximum, IntervalMaximum, WorstFrameInterval;
        internal long Deferrals, CombatDeferrals;
        internal readonly double[] WorstPhases = new double[6];
        internal readonly PerformanceStages.ArrivalSample[] Peaks = new PerformanceStages.ArrivalSample[6];
        internal int PeakCount;
    }
    private readonly IPlayerPresence _presence;
    private readonly DebugLoggingPolicy _policy;
    private readonly ManualLogSource _log;
    private readonly PlayerWeatherContext _weather;
    private readonly Func<Vector3, (string Environment, bool? Wet, bool? ZoneLoaded)> _destination;
    private readonly Dictionary<ZNetPeer, State> _states = new();
    private readonly List<Window> _windows = new();
    private readonly List<ZNetPeer> _remove = new();
    private readonly Action<PerformanceStages.ArrivalSample> _sample;
    private readonly double[] _phases = new double[6];
    private ZDOMan? _world;
    private float _next, _last = float.NegativeInfinity;
    private long _session, _id, _roster, _omitted, _lastDeferrals, _lastCombatDeferrals;
    private bool _failed;
    internal PlayerArrivalDiagnostics(IPlayerPresence presence, DebugLoggingPolicy policy, ManualLogSource log, PlayerWeatherContext weather,
        Func<Vector3, (string Environment, bool? Wet, bool? ZoneLoaded)>? destination = null)
    { _presence = presence; _policy = policy; _log = log; _weather = weather; _sample = Sample; _destination = destination ?? ReadDestination; PerformanceStages.CaptureArrivals = policy.ConfiguredEnabled; }
    internal void Observe(float now)
    {
        if (_failed) return;
        try
        {
            if (!ReferenceEquals(_world, ZDOMan.instance) || now < _last)
            { Clear(); _world = ZDOMan.instance; }
            _last = now;
            PerformanceStages.CaptureArrivals = _policy.ConfiguredEnabled;
            if (!_policy.ConfiguredEnabled || !WorldServices.World || _presence.HasConnectedPlayers == null)
            { Clear(); return; }
            if (_presence.HasConnectedPlayers == false)
            {
                foreach (var state in _states.Values) Close(state, now, "peer_departed");
                _states.Clear(); return;
            }
            bool samplePosition = now >= _next;
            if (samplePosition) _next = now + .2f;
            _roster++;
            int count = Math.Min(64, _presence.Peers.Count);
            for (int i = 0; i < count; i++)
            {
                var peer = _presence.Peers[i];
                var state = Get(peer); if (state == null) continue;
                state.Seen = _roster;
                if (!peer.IsReady() || peer.m_characterID.IsNone()) { state.Positioned = false; continue; }
                bool announced = ObserveCharacter(peer, peer.m_characterID, now);
                if (!samplePosition && !announced) continue;
                var actor = peer.m_characterID.IsNone() ? null : _world?.GetZDO(peer.m_characterID);
                if (!peer.IsReady() || actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid ||
                    !PlayerWorldScope.Finite(actor.GetPosition()) || !NativeCharacterHealth.IsAlive(actor))
                { state.Positioned = false; continue; }
                ObservePosition(peer, peer.m_characterID, actor!.GetPosition(), now);
                if (state.Active is { End.Length: 0 } arrival && float.IsNegativeInfinity(arrival.FirstZoneLoaded) &&
                    ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(arrival.To)) arrival.FirstZoneLoaded = now;
            }
            _remove.Clear();
            foreach (var pair in _states)
                if (pair.Value.Seen != _roster) { Close(pair.Value, now, "peer_departed_or_roster_limited"); _remove.Add(pair.Key); }
            foreach (var peer in _remove) _states.Remove(peer);
        }
        catch (Exception error) { Fail(error); }
    }
    // Start before feature activation, even if the announced character record has not arrived yet.
    internal bool ObserveCharacter(ZNetPeer peer, ZDOID actor, float now)
    {
        if (!_policy.ConfiguredEnabled || actor.IsNone()) return false;
        var state = Get(peer); if (state == null || state.Actor == actor) return false;
        string kind = state.Actor.IsNone() ? "first_character_announced" : "character_change_announced";
        Close(state, now, "superseded_by_character_announcement");
        if (_windows.Count < 64)
        {
            var window = new Window { Id = ++_id, Session = state.Session, Peer = peer.m_uid, Actor = actor,
                Name = PlayerConnectionNotices.CleanName(peer.m_playerName), Kind = kind, Started = now, Gap = -1, DestinationObserved = false };
            _windows.Add(window); state.Active = window;
        }
        else _omitted++;
        state.Actor = actor; state.Positioned = false; return true;
    }
    internal bool DeferPinComparison(Vector3 point, float now)
    {
        foreach (var window in _windows)
            if (window.End.Length == 0 && now >= window.Started && now - window.Started < 2 &&
                (!window.DestinationObserved || (window.To - point).sqrMagnitude <= 128 * 128))
            { window.PinComparisonsSkipped++; return true; }
        return false;
    }
    private State? Get(ZNetPeer peer)
    {
        if (_states.TryGetValue(peer, out var state)) return state;
        if (_states.Count >= 64) { _omitted++; return null; }
        _states.Add(peer, state = new State { Session = ++_session }); return state;
    }
    // Called only after the native actor is validated; separated for deterministic transition coverage.
    internal void ObservePosition(ZNetPeer peer, ZDOID actor, Vector3 position, float now)
    {
        if (!_policy.ConfiguredEnabled || actor.IsNone() || !PlayerWorldScope.Finite(position)) return;
        var state = Get(peer); if (state == null) return;
        if (state.Active is { DestinationObserved: false } pending && pending.Actor == actor)
        {
            pending.To = position; pending.DestinationObserved = true;
            state.Position = position; state.At = now; state.Positioned = true; return;
        }
        float gap = now - state.At;
        string? kind = state.Actor.IsNone() ? "first_actor_observed" : state.Actor != actor ? "character_changed" :
            !state.Positioned ? "actor_recovered" : (position - state.Position).sqrMagnitude >= 64 * 64 ?
                gap >= 0 && gap <= 2 ? "position_jump_inferred" : "relocation_after_observation_gap" : null;
        if (kind != null)
        {
            Close(state, now, "superseded_by_arrival");
            if (_windows.Count < 64)
            {
                var window = new Window { Id = ++_id, Session = state.Session, Peer = peer.m_uid, Actor = actor,
                    Name = PlayerConnectionNotices.CleanName(peer.m_playerName), Kind = kind, To = position,
                    From = state.Positioned && state.Actor == actor ? state.Position : (Vector3?)null,
                    Started = now, Gap = state.Positioned ? gap : -1 };
                _windows.Add(window); state.Active = window;
            }
            else _omitted++;
        }
        state.Actor = actor; state.Position = position; state.At = now; state.Positioned = true;
    }
    private static void Close(State state, float now, string reason)
    { if (state.Active != null && state.Active.End.Length == 0) { state.Active.End = reason; state.Active.Closed = now; } state.Active = null; }
    internal void EndFrame(float now, long started)
    {
        if (_failed) { PerformanceStages.EndArrivalFrame(null); return; }
        try
        {
            Capture(now, ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - started), ServerPerformance.CurrentInterval,
                GC.CollectionCount(0) - ServerPerformance.UpdateCollections, ServerPerformance.UpdateSequence);
        }
        catch (Exception error) { Fail(error); }
    }
    internal void Capture(float now, double milliseconds, double interval, int gc, long frame)
    {
        Array.Clear(_phases, 0, _phases.Length);
        PerformanceStages.EndArrivalFrame(_windows.Count > 0 ? _sample : null);
        long deferrals = ModuleScheduler.Current.Deferrals, combat = ModuleScheduler.Current.CombatDeferrals;
        long deferred = Math.Max(0, deferrals - _lastDeferrals), combatDeferred = Math.Max(0, combat - _lastCombatDeferrals);
        _lastDeferrals = deferrals; _lastCombatDeferrals = combat;
        int active = 0; foreach (var window in _windows) if (window.End.Length == 0) active++;
        foreach (var window in _windows)
        {
            if (window.End.Length == 0)
            {
                if (window.DestinationObserved && float.IsNegativeInfinity(window.FirstCached) && WorldObjectProvider.Current != null &&
                    WorldObjectProvider.Current.TryCachedCell(ZoneSystem.GetZone(window.To), out _)) window.FirstCached = now;
                window.Frames++; window.Total += milliseconds; window.Concurrent = Math.Max(window.Concurrent, active);
                window.IntervalMaximum = Math.Max(window.IntervalMaximum, interval);
                window.Deferrals += deferred; window.CombatDeferrals += combatDeferred;
                if (milliseconds >= window.Maximum)
                {
                    window.Maximum = milliseconds; window.WorstFrame = frame; window.WorstGc = gc; window.WorstFrameInterval = interval;
                    Array.Copy(_phases, window.WorstPhases, _phases.Length);
                }
                if (now - window.Started >= 10) { window.End = "observation_window_elapsed"; window.Closed = now; }
            }
            bool final = window.End.Length > 0;
            if (final || !window.Interim && now - window.Started >= 2)
            {
                if (DiagnosticLog.ArrivalAvailable(_log, _policy) && Report(now, window, final))
                { if (final) window.Closed = float.NegativeInfinity; else window.Interim = true; }
                else if (final && now - window.Closed >= 2) { _omitted++; window.Closed = float.NegativeInfinity; }
            }
        }
        for (int i = _windows.Count - 1; i >= 0; i--)
            if (float.IsNegativeInfinity(_windows[i].Closed)) _windows.RemoveAt(i);
    }
    private void Sample(PerformanceStages.ArrivalSample sample)
    {
        if (sample.Module == "HeimKit")
        {
            int phase = sample.Stage == "world_services" ? 0 : sample.Stage == "player_services" ? 1 :
                sample.Stage == "world_preparation" ? 2 : sample.Stage == "feature_dispatch" ? 3 :
                sample.Stage == "player_reporting" ? 4 : sample.Stage == "player_diagnostics" ? 5 : -1;
            if (phase >= 0) _phases[phase] += sample.Total;
        }
        foreach (var window in _windows)
        {
            if (window.End.Length > 0) continue;
            int slot = -1, smallest = 0;
            for (int i = 0; i < window.PeakCount; i++)
            {
                if (window.Peaks[i].Module == sample.Module && window.Peaks[i].Stage == sample.Stage) { slot = i; break; }
                if (window.Peaks[i].Maximum < window.Peaks[smallest].Maximum) smallest = i;
            }
            if (slot >= 0) { if (sample.Maximum > window.Peaks[slot].Maximum) window.Peaks[slot] = sample; }
            else if (window.PeakCount < window.Peaks.Length) window.Peaks[window.PeakCount++] = sample;
            else if (sample.Maximum > window.Peaks[smallest].Maximum) window.Peaks[smallest] = sample;
        }
    }
    private bool Report(float now, Window window, bool final)
    {
        var zone = ZoneSystem.GetZone(window.To);
        var provider = WorldObjectProvider.Current;
        string cached = window.DestinationObserved && provider != null && provider.TryCachedCell(zone, out var records) ? records.Count.ToString(CultureInfo.InvariantCulture) : "unavailable";
        var destination = window.DestinationObserved ? _destination(window.To) : (Environment: "unknown", Wet: (bool?)null, ZoneLoaded: (bool?)null);
        string wet = destination.Wet?.ToString() ?? "unknown";
        string terrain = destination.ZoneLoaded?.ToString() ?? "unknown";
        var stages = new StringBuilder("scope=shared_between_updates maxima_overlap=true");
        for (int i = 0; i < window.PeakCount; i++)
        {
            var peak = window.Peaks[i];
            stages.Append(' ').Append(peak.Module).Append('.').Append(peak.Stage).Append("_max_ms=").Append(F(peak.Maximum))
                .Append("[items=").Append(Count(peak.Items)).Append(",bytes=").Append(Count(peak.Bytes)).Append(",gc0=").Append(Count(peak.Collections));
            if (peak.Subject != null) stages.Append(",subject=").Append(peak.Subject);
            stages.Append(']');
        }
        string phase = ""; string[] names = { "services", "players", "world", "features", "reporting", "diagnostics" };
        for (int i = 0; i < names.Length; i++) phase += " " + names[i] + "_ms=" + F(window.WorstPhases[i]);
        return DiagnosticLog.WriteArrival(_log, _policy, window.Name, new[] {
            DiagnosticLog.Field("player", window.Name + " peer=" + window.Peer + " session=" + window.Session + " character=" + window.Actor),
            DiagnosticLog.Field("arrival", "id=" + window.Id + " kind=" + window.Kind + " from=" + (window.From.HasValue ? Point(window.From.Value) : "unobserved") +
                " to=" + (window.DestinationObserved ? Point(window.To) : "unobserved") + " distance_m=" + (window.From.HasValue ? F((window.To - window.From.Value).magnitude) : "unobserved") + " observation_gap_s=" + (window.Gap < 0 ? "unobserved" : F(window.Gap))),
            DiagnosticLog.Field("window", "status=" + (final ? window.End : "interim") + " age_s=" + F((final ? window.Closed : now) - window.Started) + " frames=" + window.Frames +
                " concurrent_arrivals=" + window.Concurrent + " omitted_summaries_or_samples=" + _omitted),
            DiagnosticLog.Field("work", "scope=server_shared_before_arrival_output avg_ms=" + F(window.Frames == 0 ? 0 : window.Total / window.Frames) + " max_ms=" + F(window.Maximum) +
                " max_frame=" + window.WorstFrame + " max_frame_gc0=" + window.WorstGc + " preceding_interval_ms=" + F(window.WorstFrameInterval) +
                " interval_max_ms=" + F(window.IntervalMaximum) + phase + " unphased_ms=" + F(Unphased(window)) + " deferred=" + window.Deferrals + " combat_deferred=" + window.CombatDeferrals + " pin_comparisons_skipped=" + window.PinComparisonsSkipped),
            DiagnosticLog.Field("stages", stages.ToString() + " pending_work=" + PerformanceStages.ArrivalProgress(now)),
            DiagnosticLog.Field("destination", "zone=" + (window.DestinationObserved ? zone.ToString() : "unobserved") + " server_zone_loaded=" + terrain + " cached_zone_records=" + cached +
                " zone_loaded_first_observed_s=" + Since(window.FirstZoneLoaded, window.Started) + " cached_first_observed_s=" + Since(window.FirstCached, window.Started) +
                " server_environment=" + destination.Environment + " server_native_wet=" + wet + " combat_pause=" + ModuleScheduler.Current.CombatActive),
            DiagnosticLog.Field("weather_guard", window.DestinationObserved ? _weather.Read(now, zone, out _) : "coverage=destination_unobserved"),
            DiagnosticLog.Field("limits", "portal_use=unconfirmed client_loading=unobservable local_weather=unverified pending_feature_jobs=partial timings=elapsed_not_cpu") });
    }
    private static string Count(long value) => value < 0 ? "unavailable" : value.ToString(CultureInfo.InvariantCulture);
    private static (string Environment, bool? Wet, bool? ZoneLoaded) ReadDestination(Vector3 point)
    {
        var env = EnvMan.instance?.GetCurrentEnvironment();
        return (env?.m_name ?? "unknown", env == null ? (bool?)null : EnvMan.IsWet(),
            ZoneSystem.instance == null ? (bool?)null : ZoneSystem.instance.IsZoneLoaded(point));
    }
    private static double Unphased(Window window) { double phases = 0; foreach (double value in window.WorstPhases) phases += value; return Math.Max(0, window.Maximum - phases); }
    private static string Since(float at, float started) => float.IsNegativeInfinity(at) ? "unobserved" : F(at - started);
    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Point(Vector3 value) => value.x.ToString("F1", CultureInfo.InvariantCulture) + "," + value.y.ToString("F1", CultureInfo.InvariantCulture) + "," + value.z.ToString("F1", CultureInfo.InvariantCulture);
    private void Fail(Exception error)
    {
        _failed = true; Clear(); PerformanceStages.CaptureArrivals = false;
        try { DiagnosticLog.Write(_log, "PlayerActivity", CoreLogTopics.Scope, "Arrival diagnostics stopped; gameplay scheduling continues unchanged.", level: LogLevel.Error, error: error); } catch { }
    }
    private void Clear()
    { _states.Clear(); _windows.Clear(); _remove.Clear(); _next = 0; PerformanceStages.ClearArrivalProgress(); _lastDeferrals = ModuleScheduler.Current.Deferrals; _lastCombatDeferrals = ModuleScheduler.Current.CombatDeferrals; }
    public void Dispose() { Clear(); _world = null; PerformanceStages.CaptureArrivals = false; PerformanceStages.ClearArrivalProgress(); PerformanceStages.EndArrivalFrame(null); }
}
