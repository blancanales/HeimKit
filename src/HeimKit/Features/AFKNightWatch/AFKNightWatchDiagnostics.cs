using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using UnityEngine;
using HeimKit.Features.AFKNightWatch.Models;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatchDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly SummaryCadence _summary = new();
    public bool Enabled => DiagnosticsEnabled;
    private float _lastWarning = float.NegativeInfinity;
    private string? _lastNativeKey;
    private long _unchangedNative;
    public AFKNightWatchDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "AFKNightWatch"); }
    private void Write(LogTopic topic, string message, LogFields fields = default, LogLevel level = LogLevel.Debug, bool verbose = false)
        => DiagnosticLog.Write(_log, "AFKNightWatch", topic, message, fields, level, verbose: verbose);
    public void SuspendReports() { _lastNativeKey = null; _summary.Reset(); }
    public bool ReadyToPoll(float now) => Enabled && _summary.PollReady(now);
    public void Report(float now, Func<NightWatchStatus> snapshot)
    {
        if (!Enabled) { _lastNativeKey = null; _summary.Reset(); return; }
        if (!_summary.CanPoll(now)) return;
        var state = snapshot();
        if (state.Players == 0 || state.Vote == null) { _summary.Reset(); return; }
        var vote = state.Vote; var outcome = state.SleepOutcome;
        string key = $"{state.Players}:{vote.Participants}:{vote.Sleepers}:{vote.Required}:{vote.ExcludedIdle}:{vote.Met}:{vote.Unknown}:{outcome?.Outcome}:{state.Outcomes}";
        if (!_summary.ShouldEmit(now, key)) return;
        if (!_summary.Changed)
        {
            Write(CoreLogTopics.Scope, "Relevant observations remain unchanged.", new (string, object?)[] { ("observation_age_s", state.VoteAge), ("omitted_messages", _budget.TakeOmitted()) });
            return;
        }
        { Write(CoreLogTopics.Scope, vote.Unknown ? "Unknown actor data; native fallback depends on a living sleeper" : vote.Met ? "Sleep quorum met; native time checks still apply" : "Sleep quorum not met", new (string, object?)[] { ("majority_enabled", state.MajorityEnabled), ("afk_sleep_exclusion_enabled", state.ExclusionEnabled), ("participants_last_vote", vote.Participants), ("sleepers_last_vote", vote.Sleepers), ("required_last_vote", vote.Required), ("excluded_idle_last_vote", vote.ExcludedIdle), ("quorum_met_last_vote", vote.Met), ("delay_remaining_s", vote.DelayRemaining) }); if (VerboseEnabled) Write(CoreLogTopics.Scope, "Sleep vote evidence for the last minute.", new (string, object?)[] { ("allowed_after_delay", vote.AllowedAfterDelay), ("native_vote_fallback", vote.Unknown && vote.Sleepers > 0), ("lifetime_votes_observed", state.Votes), ("vote_age_s", state.VoteAge), ("time_skip", outcome?.Outcome ?? state.Sleep?.Outcome ?? "not_observed"), ("last_outcome_age_s", outcome == null ? (float?)null : Math.Max(0, now - outcome.ObservedAt)), ("native_probe_age_s", state.Sleep == null ? (float?)null : Math.Max(0, now - state.Sleep.ObservedAt)), ("unchanged_native_samples", TakeUnchangedNative()), ("omitted_messages", _budget.TakeOmitted()) }, verbose: true); }
        if (_budget.Reserve(now, "hook_activity", 1, false)) Write(AFKNightWatchLogTopics.Sleep, "Observed sleep-hook activity in the last minute.", new (string, object?)[] { ("lifetime_sleep_hook_counts", state.Hooks?.Format() ?? "not_observed") });
    }
    public void SleepStarted(SleepDecision vote, AFKNightWatchSettings settings)
    {
        if (!Enabled) return;
        { Write(AFKNightWatchLogTopics.Sleep, "Configured sleep rules permitted the observed sleep start.", new (string, object?)[] { ("majority_enabled", settings.MajoritySleepEnabled.Value), ("required_sleeping_percent", settings.RequiredSleepingPercent.Value), ("require_more_than_percent", settings.RequireMoreThanPercent.Value), ("exclude_afk_from_sleep", settings.ExcludeAFKFromSleep.Value), ("sleep_delay_s", settings.SleepDelaySeconds.Value), ("delay_remaining_s", vote.DelayRemaining), ("joined_players", vote.Participants + vote.ExcludedIdle), ("participants", vote.Participants) }); if (VerboseEnabled) Write(AFKNightWatchLogTopics.Sleep, "Sleep start evidence for the observed vote.", new (string, object?)[] { ("sleepers", vote.Sleepers), ("required_sleepers", vote.Required), ("excluded_afk", vote.ExcludedIdle), ("quorum_met", vote.Met) }, verbose: true); }
    }
    public void Warning(float now, Exception error)
    {
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        Write(CoreLogTopics.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
    private long TakeUnchangedNative() { long result = _unchangedNative; _unchangedNative = 0; return result; }
    public void Native(NativeSleepObservation evidence)
    {
        if (!DiagnosticsEnabled) return;
        string key = evidence.Gate + ":" + evidence.Outcome;
        if (_lastNativeKey == key) { _unchangedNative++; return; }
        if (!_budget.Reserve(evidence.ObservedAt, "native_sleep", 6)) return;
        _lastNativeKey = key;
        { Write(AFKNightWatchLogTopics.Sleep, "Observed the native sleep update outcome.", new (string, object?)[] { ("gate", evidence.Gate), ("outcome", evidence.Outcome), ("server", evidence.Server), ("sleeping_before", evidence.Sleeping), ("sleeping_after", evidence.SleepingAfter), ("server_game_time_before_s", evidence.GameTimeBefore), ("server_game_time_after_s", evidence.GameTimeAfter), ("skipping_before", evidence.SkippingBefore) }); if (VerboseEnabled) Write(AFKNightWatchLogTopics.Sleep, $"Native sleep update evidence for outcome {evidence.Outcome}.", new (string, object?)[] { ("skipping_after", evidence.SkippingAfter), ("afternoon", evidence.Afternoon), ("night", evidence.Night), ("cooldown_remaining_s", evidence.CooldownRemaining), ("native_exception", evidence.Failed), ("cause", "unknown"), ("clients_acknowledged", "unobservable") }, verbose: true); }
    }
}
