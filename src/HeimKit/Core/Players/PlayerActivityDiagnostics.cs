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
using HeimKit.Core.Players.Models;
namespace HeimKit.Core.Players;
// One bounded shared stream; its caller combines shared and consumer debug policies.
public sealed class PlayerActivityDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly Func<bool> _enabled;
    private readonly ActivitySamples _samples = new();
    private readonly DiagnosticBudget _budget = new();
    public PlayerActivityDiagnostics(ManualLogSource log, Func<bool> enabled) { _log = log; _enabled = enabled; }
    public void Players(float now, IEnumerable<PlayerActivityView> players)
    {
        if (!_enabled() || !DiagnosticLog.ServerActivity.Allowed) { Reset(); return; }
        _samples.Capture(now, players);
        if (!_budget.Reserve(now, "players", 1, false)) return;
        var changed = _samples.Drain(6);
        if (changed.Count == 0) return;
        if (DiagnosticLog.DebugAvailable(_log, "PlayerActivity")) DiagnosticLog.Write(_log, "PlayerActivity", CoreLogTopics.Scope, "Observed player activity transitions in the last minute.", new (string, object?)[] { ("transitions", _samples.TransitionsInterval), ("pending_states_replaced", _samples.ReplacedPendingInterval), ("pending_states", _samples.Pending), ("actor_samples", changed.Count) }, LogLevel.Debug);
        _samples.IntervalReported();
        foreach (var p in changed)
            { if (DiagnosticLog.DebugAvailable(_log, "PlayerActivity")) DiagnosticLog.Write(_log, "PlayerActivity", PlayerLogTopics.Afk, $"{p.Name}'s activity state changed — {p.Reason.Replace('_', ' ')}.", new (string, object?)[] { ("peer", p.Peer), ("session", p.Session), ("state_revision", p.StateRevision), ("known", p.Known), ("alive", p.Alive), ("in_bed", p.InBed), ("inferred_idle", p.Idle), ("idle_observed_s", p.IdleSeconds) }, LogLevel.Debug); if (DiagnosticLog.VerboseConfigured && DiagnosticLog.DebugAvailable(_log, "PlayerActivity", true)) DiagnosticLog.Write(_log, "PlayerActivity", PlayerLogTopics.Afk, $"Activity evidence for {p.Name}.", new (string, object?)[] { ("last_observed_signal", p.LastSignal), ("grace_remaining_s_at_sample", p.GraceRemaining), ("state_sample_age_s", Math.Max(0, now - p.ObservedAt)), ("recovery", p.Recovery) }, LogLevel.Debug, verbose: true); }
    }
    public void Reset() { _samples.Reset(); }
}
