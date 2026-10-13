using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKNightWatch.Models;
using HeimKit.Shared;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace HeimKit.Features.AFKNightWatch;

public static class AFKNightWatchSleepVoteCoordinator
{
    private static readonly Harmony Hooks = new("heimkit.sleep_vote");
    private static readonly Harmony Observers = new("heimkit.sleep_vote.diagnostics");
    private static readonly List<string> Registered = new();
    public static string RegisteredHooks => string.Join(",", Registered);
    private static Func<(int Percentage, bool Strict)>? _majority;
    private static IPlayerActivityObservations? _activity;
    private static IPlayerPresence? _presence;
    private static Action<SleepDecision>? _result;
    private static Action<Exception>? _failure;
    private static bool _installed;
    private static Func<bool>? _debug;
    private static Action<bool, bool?, SleepDecision?>? _lifecycle;
    private static readonly AFKNightWatchSleepDelay SleepDelay = new();
    private static Func<int>? _delaySeconds;
    private static SleepDecision? _acceptedVote;
    private static Action<string, NativeSleepObservation?>? _hook;
    private static ManualLogSource? _logger;
    private static string _logName = "SleepVote";
    private static float _lastAttemptAt = float.NegativeInfinity;
    private static float _lastWarning = float.NegativeInfinity;
    public static IDisposable Register(Func<(int, bool)>? options, IPlayerActivityObservations? activity, Action<SleepDecision> result, Action<Exception> failure, Func<bool>? debug = null, Action<string, NativeSleepObservation?>? hook = null, ManualLogSource? logger = null, string logName = "SleepVote", IPlayerPresence? presence = null, Action<bool, bool?, SleepDecision?>? lifecycle = null, Func<int>? delaySeconds = null)
    {
        if (_installed) throw new InvalidOperationException("A sleep-vote owner is already registered.");
        _logger = logger; _logName = logName; _presence = presence ?? throw new ArgumentNullException(nameof(presence));
        try { Install(); _debug = debug; _hook = hook; _lifecycle = lifecycle; _delaySeconds = delaySeconds; SleepDelay.Reset(); _acceptedVote = null; _majority = options; _activity = activity; _result = result; _failure = failure; }
        catch
        {
            _presence = null; _majority = null; _activity = null; _result = null; _failure = null; _debug = null; _hook = null; _lifecycle = null; _delaySeconds = null; SleepDelay.Reset(); _acceptedVote = null;
            UninstallUnused(); throw;
        }
        return new Registration(() => { _lastAttemptAt = float.NegativeInfinity; _presence = null; _debug = null; _hook = null; _lifecycle = null; _delaySeconds = null; SleepDelay.Reset(); _acceptedVote = null; _majority = null; _activity = null; _result = null; _failure = null; UninstallUnused(); });
    }
    private static void Install()
    {
        if (_installed) return;
        try
        {
            Hooks.Patch(AccessTools.Method(typeof(Game), "EverybodyIsTryingToSleep"), prefix: new HarmonyMethod(typeof(AFKNightWatchSleepVoteCoordinator), nameof(Vote)));
            Registered.Clear(); Registered.Add("Game.EverybodyIsTryingToSleep:vote_control"); _installed = true;
        }
        catch { Hooks.UnpatchSelf(); throw; }
        InstallObserver(typeof(Game), "EverybodyIsTryingToSleep", postfix: nameof(VoteReturned), finalizer: nameof(VoteFinished));
        InstallObserver(typeof(Game), "UpdateSleeping", prefix: nameof(UpdateEntered), finalizer: nameof(UpdateFinished));
        InstallObserver(typeof(EnvMan), "SkipToMorning", prefix: nameof(SkipRequested));
    }
    private static void InstallObserver(Type type, string method, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        MethodInfo? target = null;
        try
        {
            target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.Name, method);
            Observers.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(AFKNightWatchSleepVoteCoordinator), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(AFKNightWatchSleepVoteCoordinator), postfix), finalizer: finalizer == null ? null : new HarmonyMethod(typeof(AFKNightWatchSleepVoteCoordinator), finalizer));
            Registered.Add(type.Name + "." + method + ":observation");
        }
        catch (Exception error)
        {
            if (target != null) Observers.Unpatch(target, HarmonyPatchType.All, Observers.Id);
            if (_logger != null) DiagnosticLog.Write(_logger, _logName, DiagnosticEvent.Lifecycle, "Diagnostic hook unavailable; gameplay hook retained", new (string, object?)[] { ("hook", type.Name + "." + method), ("registered", false), ("reason", error.Message) }, level: LogLevel.Warning);
        }
    }
    private static void UninstallUnused()
    {
        if (_majority != null || _activity != null) return;
        _installed = false;
        try { Observers.UnpatchSelf(); }
        finally
        {
            try { Hooks.UnpatchSelf(); }
            finally { Registered.Clear(); _logger = null; }
        }
    }
    private static bool Vote(ref bool __result)
    {
        Notify("vote_hook_calls");
        if (!PlayerServices.Ready || !_installed || _presence?.HasJoinedPlayers != true || EnvMan.instance == null || !(EnvMan.IsAfternoon() || EnvMan.IsNight())) { SleepDelay.Reset(); _acceptedVote = null; Notify("vote_not_ready_fallback"); return true; }
        try
        {
            var players = _activity != null ? _activity.Snapshot(Time.unscaledTime) :
                PlayerActivityService.ReadActors(_presence!).Select(p => new PlayerActivityView { Peer = p.Peer, Known = p.Known, Alive = p.Alive, InBed = p.InBed }).ToArray();
            if (_activity != null && !_activity.Reliable) { SleepDelay.Reset(); _acceptedVote = null; Notify("vote_not_ready_fallback"); return true; }
            if (!players.Any(p => p.Known && p.Alive && p.InBed)) { SleepDelay.Reset(); _acceptedVote = null; Notify("no_living_sleeper_votes"); __result = false; return false; }
            _lastAttemptAt = Time.unscaledTime;
            var settings = _majority?.Invoke() ?? (100, false);
            var decision = AFKNightWatchSleepQuorumRules.Evaluate(players, settings.Item1, settings.Item2, _activity != null);
            decision.AllowedAfterDelay = SleepDelay.Allow(Time.unscaledTime, decision.Met,
                players.All(p => p.Known && p.InBed), _delaySeconds?.Invoke() ?? 30);
            decision.DelayRemaining = SleepDelay.Remaining;
            _acceptedVote = decision.AllowedAfterDelay ? decision : null;
            _result?.Invoke(decision);
            if (decision.Participants == 0) Notify("empty_vote_rosters");
            if (decision.Sleepers == 0) { SleepDelay.Reset(); _acceptedVote = null; Notify("no_living_sleeper_votes"); __result = false; return false; }
            if (decision.Unknown) { Notify("unknown_actor_native_fallbacks"); return true; }
            __result = decision.AllowedAfterDelay; return false;
        }
        catch (Exception error)
        {
            SleepDelay.Reset(); _acceptedVote = null;
            Notify("vote_processing_failures");
            _failure?.Invoke(error); return true;
        }
    }
    private static bool DebugEnabled => _debug?.Invoke() == true;
    private static void Notify(string hook, NativeSleepObservation? evidence = null)
    {
        try { _hook?.Invoke(hook, evidence); } catch (Exception error) { ObservationWarning(error); }
    }
    private static void ObservationWarning(Exception error)
    {
        float now = Time.unscaledTime;
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        if (_logger != null) DiagnosticLog.Write(_logger, _logName, DiagnosticEvent.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
    private static void VoteReturned(bool __result) => Notify(__result ? "vote_returned_true" : "vote_returned_false");
    private static Exception? VoteFinished(Exception? __exception) { if (__exception != null) Notify("native_vote_exceptions"); return __exception; }
    private static void SkipRequested() => Notify("skip_to_morning_invocations");
    private static void ObserveLifecycle(bool sleeping, bool failed = false, SleepDecision? vote = null)
    {
        try
        {
            bool? skipping = !failed && _presence?.HasJoinedPlayers == true && ZNet.instance != null && ZNet.instance.IsServer() && EnvMan.instance != null
                ? EnvMan.instance.IsTimeSkipping() : (bool?)null;
            _lifecycle?.Invoke(sleeping, skipping, failed ? null : vote);
        }
        catch (Exception error) { ObservationWarning(error); }
    }
    private static void UpdateEntered(bool ___m_sleeping, double ___m_lastSleepTime, out NativeSleepObservation? __state)
    {
        __state = null; Notify("update_sleeping_invocations");
        _acceptedVote = null;
        if (___m_sleeping || _presence?.HasJoinedPlayers != true || EnvMan.instance == null ||
            EnvMan.instance.IsTimeSkipping() || !(EnvMan.IsAfternoon() || EnvMan.IsNight())) SleepDelay.Reset();
        ObserveLifecycle(___m_sleeping);
        try
        {
            if (!DebugEnabled || _presence?.HasJoinedPlayers != true) return;
            if (!___m_sleeping && (EnvMan.instance == null || !(EnvMan.IsAfternoon() || EnvMan.IsNight()))) return;
            __state = new NativeSleepObservation { ObservedAt = Time.unscaledTime, Server = ZNet.instance != null ? ZNet.instance.IsServer() : (bool?)null, Sleeping = ___m_sleeping };
            if (EnvMan.instance != null)
            { __state.SkippingBefore = EnvMan.instance.IsTimeSkipping(); __state.Afternoon = EnvMan.IsAfternoon(); __state.Night = EnvMan.IsNight(); }
            if (ZNet.instance != null)
            {
                double clock = ZNet.instance.GetTimeSeconds();
                if (!double.IsNaN(clock) && !double.IsInfinity(clock) && !double.IsNaN(___m_lastSleepTime) && !double.IsInfinity(___m_lastSleepTime))
                { __state.GameTimeBefore = clock; __state.CooldownRemaining = Math.Max(0, 10 - (clock - ___m_lastSleepTime)); }
            }
        }
        catch (Exception error) { Notify("sleep_probe_failures"); ObservationWarning(error); }
    }
    private static Exception? UpdateFinished(NativeSleepObservation? __state, bool ___m_sleeping, Exception? __exception)
    {
        if (__exception != null) SleepDelay.Reset();
        ObserveLifecycle(___m_sleeping, __exception != null, _acceptedVote);
        _acceptedVote = null;
        if (__exception != null) Notify("native_sleep_update_exceptions");
        if (__state != null)
        {
            try
            {
                __state.Failed = __exception != null; __state.SleepingAfter = ___m_sleeping;
                if (ZNet.instance != null) __state.GameTimeAfter = ZNet.instance.GetTimeSeconds();
                __state.SkippingAfter = EnvMan.instance != null ? EnvMan.instance.IsTimeSkipping() : (bool?)null;
                if (___m_sleeping || __state.Sleeping == true || Time.unscaledTime - _lastAttemptAt <= 2 || __exception != null)
                    Notify("sleep_update_observed", __state);
            }
            catch (Exception error) { Notify("sleep_probe_failures"); ObservationWarning(error); }
        }
        return __exception;
    }
    private sealed class Registration : IDisposable
    {
        private Action? _release;
        public Registration(Action release) => _release = release;
        public void Dispose() { var release = _release; _release = null; release?.Invoke(); }
    }
}
