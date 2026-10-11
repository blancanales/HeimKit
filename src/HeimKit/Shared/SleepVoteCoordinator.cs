using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace HeimKit.Shared;

public static class SleepVoteCoordinator
{
    private static readonly Harmony Hooks = new("heimkit.sleep_vote");
    private static readonly Harmony Observers = new("heimkit.sleep_vote.diagnostics");
    private static readonly List<string> Registered = new();
    public static string RegisteredHooks => string.Join(",", Registered);
    private static Func<(int Percentage, bool Strict)>? _majority;
    private static PlayerActivityService? _activity;
    private static Action<SleepDecision>? _majorityResult, _exclusionResult;
    private static Action<Exception>? _majorityFailure, _exclusionFailure;
    private static bool _installed;
    private static Func<bool>? _majorityDebug, _exclusionDebug;
    private static Action<string, NativeSleepObservation?>? _majorityHook, _exclusionHook;
    private static ManualLogSource? _logger;
    private static float _lastWarning = float.NegativeInfinity;
    public static IDisposable RegisterMajority(Func<(int, bool)> options, Action<SleepDecision> result, Action<Exception> failure, Func<bool>? debug = null, Action<string, NativeSleepObservation?>? hook = null, ManualLogSource? logger = null)
    {
        _logger = logger ?? _logger; Install(); _majorityDebug = debug; _majorityHook = hook; _majority = options; _majorityResult = result; _majorityFailure = failure;
        return new Registration(() => { _majorityDebug = null; _majorityHook = null; _majority = null; _majorityResult = null; _majorityFailure = null; UninstallUnused(); });
    }
    public static IDisposable RegisterExclusion(PlayerActivityService service, Action<SleepDecision> result, Action<Exception> failure, Func<bool>? debug = null, Action<string, NativeSleepObservation?>? hook = null, ManualLogSource? logger = null)
    {
        _logger = logger ?? _logger; Install(); _exclusionDebug = debug; _exclusionHook = hook; _activity = service; _exclusionResult = result; _exclusionFailure = failure;
        return new Registration(() => { _exclusionDebug = null; _exclusionHook = null; _activity = null; _exclusionResult = null; _exclusionFailure = null; UninstallUnused(); });
    }
    private static void Install()
    {
        if (_installed) return;
        try
        {
            Hooks.Patch(AccessTools.Method(typeof(Game), "EverybodyIsTryingToSleep"), prefix: new HarmonyMethod(typeof(SleepVoteCoordinator), nameof(Vote)));
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
            Observers.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(SleepVoteCoordinator), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(SleepVoteCoordinator), postfix), finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SleepVoteCoordinator), finalizer));
            Registered.Add(type.Name + "." + method + ":observation");
        }
        catch (Exception error)
        {
            if (target != null) Observers.Unpatch(target, HarmonyPatchType.All, Observers.Id);
            if (_logger != null) DiagnosticLog.Write(_logger, "SleepVote", DiagnosticEvent.Lifecycle, "Diagnostic hook unavailable; gameplay hook retained", new (string, object?)[] { ("hook", type.Name + "." + method), ("registered", false), ("reason", error.Message) }, level: LogLevel.Warning);
        }
    }
    private static void UninstallUnused() { if (_majority != null || _activity != null) return; Observers.UnpatchSelf(); Hooks.UnpatchSelf(); Registered.Clear(); _installed = false; }
    private static bool Vote(ref bool __result)
    {
        Notify("vote_hook_calls");
        if (!PlayerActivityService.Ready || !_installed) { Notify("vote_not_ready_fallback"); return true; }
        try
        {
            var players = _activity != null ? _activity.Snapshot(Time.unscaledTime) :
                PlayerActivityService.ReadActors().Select(p => new PlayerActivityView { Peer = p.Peer, Known = p.Known, Alive = p.Alive, InBed = p.InBed }).ToArray();
            var settings = _majority?.Invoke() ?? (100, false);
            var decision = SleepQuorumRules.Evaluate(players, settings.Item1, settings.Item2, _activity != null);
            _majorityResult?.Invoke(decision); _exclusionResult?.Invoke(decision);
            if (decision.Participants == 0) Notify("empty_vote_rosters");
            if (decision.Sleepers == 0) { Notify("no_living_sleeper_votes"); __result = false; return false; }
            if (decision.Unknown) { Notify("unknown_actor_native_fallbacks"); return true; }
            __result = decision.Met; return false;
        }
        catch (Exception error)
        {
            Notify("vote_processing_failures");
            var majority = _majorityFailure; var exclusion = _exclusionFailure;
            majority?.Invoke(error); exclusion?.Invoke(error); return true;
        }
    }
    private static bool DebugEnabled => _majorityDebug?.Invoke() == true || _exclusionDebug?.Invoke() == true;
    private static void Notify(string hook, NativeSleepObservation? evidence = null)
    {
        try { _majorityHook?.Invoke(hook, evidence); } catch (Exception error) { ObservationWarning(error); }
        try { _exclusionHook?.Invoke(hook, evidence); } catch (Exception error) { ObservationWarning(error); }
    }
    private static void ObservationWarning(Exception error)
    {
        float now = Time.unscaledTime;
        if (now >= _lastWarning && now - _lastWarning < 60) return;
        _lastWarning = now;
        if (_logger != null) DiagnosticLog.Write(_logger, "SleepVote", DiagnosticEvent.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
    private static void VoteReturned(bool __result) => Notify(__result ? "vote_returned_true" : "vote_returned_false");
    private static Exception? VoteFinished(Exception? __exception) { if (__exception != null) Notify("native_vote_exceptions"); return __exception; }
    private static void SkipRequested() => Notify("skip_to_morning_invocations");
    private static void UpdateEntered(bool ___m_sleeping, double ___m_lastSleepTime, out NativeSleepObservation? __state)
    {
        __state = null; Notify("update_sleeping_invocations");
        try
        {
            if (!DebugEnabled) return;
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
        if (__exception != null) Notify("native_sleep_update_exceptions");
        if (__state != null)
        {
            try
            {
                __state.Failed = __exception != null; __state.SleepingAfter = ___m_sleeping;
                if (ZNet.instance != null) __state.GameTimeAfter = ZNet.instance.GetTimeSeconds();
                __state.SkippingAfter = EnvMan.instance != null ? EnvMan.instance.IsTimeSkipping() : (bool?)null;
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
