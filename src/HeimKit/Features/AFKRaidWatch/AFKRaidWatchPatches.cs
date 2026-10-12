using System;
using System.Collections.Generic;
using HarmonyLib;
using BepInEx.Logging;
using System.Reflection;
using HeimKit.Features.AFKRaidWatch.Models;
using HeimKit.Shared;
using UnityEngine;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatchPatches : IDisposable
{
    private readonly Harmony _harmony = new("heimkit.afk_raid_watch");
    private readonly Harmony _observers = new("heimkit.afk_raid_watch.diagnostics");
    private readonly List<string> _registered = new();
    public string RegisteredHooks => string.Join(",", _registered);
    [ThreadStatic] private static int _automaticDepth;
    public AFKRaidWatchPatches(ManualLogSource? logger = null)
    {
        try
        {
            _harmony.Patch(AccessTools.Method(typeof(RandEventSystem), "UpdateRandomEvent", new[] { typeof(float) }), prefix: new HarmonyMethod(typeof(AFKRaidWatchPatches), nameof(Enter)), finalizer: new HarmonyMethod(typeof(AFKRaidWatchPatches), nameof(Exit)));
            _harmony.Patch(AccessTools.Method(typeof(RandEventSystem), "GetValidEventPoints"), postfix: new HarmonyMethod(typeof(AFKRaidWatchPatches), nameof(Filter)));
            _registered.Add("RandEventSystem.UpdateRandomEvent:control_scope"); _registered.Add("RandEventSystem.GetValidEventPoints:control_filter");
        }
        catch { _harmony.UnpatchSelf(); throw; }
        InstallObserver(logger, "UpdateRandomEvent", prefix: nameof(UpdateObserved));
        InstallObserver(logger, "GetValidEventPoints", prefix: nameof(CandidatesEntered), finalizer: nameof(CandidatesFinished));
        InstallObserver(logger, "GetPossibleRandomEvents", postfix: nameof(PossibleReturned));
        InstallObserver(logger, "StartRandomEvent", prefix: nameof(StartEntered), finalizer: nameof(StartFinished));
        foreach (string method in new[] { "HaveGlobalKeys", "InValidBiome", "CheckBase" }) InstallObserver(logger, method, postfix: nameof(EligibilityReturned));
        InstallObserver(logger, "SetRandomEvent", prefix: nameof(SetEntered), finalizer: nameof(SetFinished));
    }
    private void InstallObserver(ManualLogSource? logger, string method, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        MethodInfo? target = null;
        try
        {
            target = AccessTools.Method(typeof(RandEventSystem), method);
            if (target == null) throw new MissingMethodException(nameof(RandEventSystem), method);
            _observers.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(AFKRaidWatchPatches), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(AFKRaidWatchPatches), postfix), finalizer: finalizer == null ? null : new HarmonyMethod(typeof(AFKRaidWatchPatches), finalizer));
            _registered.Add("RandEventSystem." + method + ":observation");
        }
        catch (Exception error)
        {
            if (target != null) _observers.Unpatch(target, HarmonyPatchType.All, _observers.Id);
            if (logger != null) DiagnosticLog.Write(logger, "AFKRaidWatch", DiagnosticEvent.Lifecycle, "Diagnostic hook unavailable; gameplay hooks retained", new (string, object?)[] { ("hook", "RandEventSystem." + method), ("registered", false), ("reason", error.Message) }, level: LogLevel.Warning);
        }
    }
    private static void Enter(out int __state) { __state = _automaticDepth; _automaticDepth++; Note("automatic_update_invocations"); }
    private static Exception? Exit(int __state, Exception? __exception)
    { if (__exception != null) Note("native_automatic_update_exceptions"); _automaticDepth = __state; return __exception; }
    private static void Filter(RandomEvent ev, List<RandEventSystem.PlayerEventData> characters, ref List<Vector3> __result)
    {
        Note("get_valid_event_points_results");
        try { AFKRaidWatchController.Active?.NativeCandidates(characters.Count, __result.Count); }
        catch (Exception error) { AFKRaidWatchController.Active?.ObservationFailed(error); }
        if (_automaticDepth == 0) { Note("outside_automatic_scope_native_retained"); return; }
        if (!ev.m_random) { Note("non_random_native_retained"); return; }
        if (ev.m_standaloneInterval > 0) { Note("standalone_native_retained"); return; }
        var controller = AFKRaidWatchController.Active;
        try { if (controller != null) __result = controller.Filter(ev, __result); }
        catch (Exception error) { controller?.Fail(error); }
    }
    private static void Note(string key) => AFKRaidWatchController.Active?.Hook(key);
    private static void UpdateObserved(RandEventSystem __instance, float ___m_eventTimer)
    {
        try { AFKRaidWatchController.Active?.ObserveSchedule(__instance, ___m_eventTimer); }
        catch (Exception error) { AFKRaidWatchController.Active?.ObservationFailed(error); }
    }
    private static void CandidatesEntered() => Note("get_valid_event_points_invocations");
    private static Exception? CandidatesFinished(Exception? __exception) { if (__exception != null) Note("native_candidate_exceptions"); return __exception; }
    private static void PossibleReturned(List<KeyValuePair<RandomEvent, Vector3>> __result)
    { Note("possible_events_results"); if (__result == null) Note("possible_events_result_unknown"); else if (__result.Count == 0) Note("empty_possible_events_after_filter"); }
    private static void StartEntered(RandEventSystem __instance, out SetObservation? __state)
    {
        __state = null; Note(_automaticDepth > 0 ? "automatic_start_requests" : "explicit_or_other_start_requests");
        if (AFKRaidWatchController.Active?.DebugEnabled != true) return;
        try { __state = new SetObservation { Before = __instance.GetCurrentRandomEvent(), Evidence = new RaidStartObservation { ObservedAt = Time.unscaledTime, Origin = _automaticDepth > 0 ? "automatic_random" : "explicit_or_other" } }; }
        catch (Exception error) { AFKRaidWatchController.Active?.ObservationFailed(error); }
    }
    private static Exception? StartFinished(RandEventSystem __instance, SetObservation? __state, Exception? __exception)
    {
        if (__exception != null) Note("native_start_request_exceptions");
        if (__state != null)
        {
            try
            {
                var current = __instance.GetCurrentRandomEvent(); bool changed = !ReferenceEquals(__state.Before, current);
                Note(changed ? "start_returned_changed_state" : "start_returned_unchanged_state");
                AFKRaidWatchController.Active?.ObserveStartReturned(Time.unscaledTime, __state.Evidence.Origin, __state.Before?.m_name, current?.m_name, changed, __exception != null);
            }
            catch (Exception error) { AFKRaidWatchController.Active?.ObservationFailed(error); }
        }
        return __exception;
    }
    private static void EligibilityReturned(MethodBase __originalMethod, bool __result)
    {
        if (_automaticDepth == 0) return;
        string key = __originalMethod.Name == "HaveGlobalKeys" ? (__result ? "native_keys_passed" : "native_keys_rejected") :
            __originalMethod.Name == "InValidBiome" ? (__result ? "native_biome_passed" : "native_biome_rejected") : (__result ? "native_base_passed" : "native_base_rejected");
        Note(key);
    }
    private sealed class SetObservation
    {
        public RandomEvent? Before;
        public RaidStartObservation Evidence = new();
    }
    private static void SetEntered(RandEventSystem __instance, RandomEvent? ev, out SetObservation? __state)
    {
        __state = null; Note("set_random_event_invocations");
        var controller = AFKRaidWatchController.Active;
        if (controller?.DebugEnabled != true) return;
        try
        {
            __state = new SetObservation { Before = __instance.GetCurrentRandomEvent(), Evidence = new RaidStartObservation
            { ObservedAt = Time.unscaledTime, Requested = ev?.m_name ?? "none", Origin = _automaticDepth == 0 ? "explicit_or_other" : ev != null && ev.m_standaloneInterval > 0 ? "automatic_standalone" : "automatic_random" } };
        }
        catch (Exception error) { controller.ObservationFailed(error); }
    }
    private static Exception? SetFinished(RandEventSystem __instance, SetObservation? __state, Exception? __exception)
    {
        if (__exception != null) Note("native_event_setter_exceptions");
        if (__state != null)
        {
            try
            {
                var current = __instance.GetCurrentRandomEvent();
                __state.Evidence.Current = current?.m_name; __state.Evidence.StateChanged = !ReferenceEquals(__state.Before, current); __state.Evidence.Failed = __exception != null;
                AFKRaidWatchController.Active?.ObserveOutcome(__state.Evidence);
            }
            catch (Exception error) { AFKRaidWatchController.Active?.ObservationFailed(error); }
        }
        return __exception;
    }
    public void Dispose()
    {
        try { _observers.UnpatchSelf(); }
        finally { try { _harmony.UnpatchSelf(); } finally { _registered.Clear(); } }
    }
}
