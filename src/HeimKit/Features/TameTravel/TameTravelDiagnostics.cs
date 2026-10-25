using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.TameTravel.Models;
using System.Linq;
namespace HeimKit.Features.TameTravel;
internal sealed class TameTravelDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private readonly System.Func<float> _clock;
    private readonly DiagnosticBudget _commands = new();
    private readonly DiagnosticBudget _selection = new();
    private readonly DiagnosticBudget _traversal = new();
    private readonly System.Collections.Generic.Dictionary<ZNetPeer, float> _sourcePollTimes = new();
    private readonly System.Collections.Generic.Dictionary<(long Peer, string Stage, ZDOID Source, ZDOID Animal), (string Reason, float Time)> _gates = new();
    public bool SelectionEnabled => DiagnosticsEnabled;
    private readonly System.Collections.Generic.Dictionary<long, string?> _actorReasons = new();
    private readonly ScheduleToken _next = new();
    private int _samples;
    private int _followCalls;
    private double _followTotal, _followMaximum;
    private readonly ScheduleToken _followNext = new();
    private (int Bindings, int Reserved, int Transfers) _last;
    public TameTravelDiagnostics(ManualLogSource log, DebugLoggingPolicy policy, System.Func<float>? clock = null)
    { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "TameTravel"); _timing = new(log, "TameTravel", policy); _clock = clock ?? (() => UnityEngine.Time.unscaledTime); }
    private bool Sample()
    {
        if (!DiagnosticsEnabled) return false;
        float now = _clock();
        if (_next.Ready(now)) { _next.Postpone(now, 60); _samples = 0; }
        return _samples++ < 2;
    }
    // Placement waits have their own allowance so a slow search cannot hide entry and arrival evidence.
    private bool TravelSample(string stream = "traversal") => DiagnosticsEnabled && _traversal.Reserve(_clock(), stream, 6);
    public bool TraversalReady(ZNetPeer peer, string stage, string reason, ZDOID? source = null, ZDOID? animal = null, bool transition = false)
    {
        if (!DiagnosticsEnabled) return false;
        bool routine = (stage is "source_entry" or "source_observation") &&
            (reason is "outside_trigger" or "no_portal_nearby" or "awaiting_owner_position" or "source_sample_gap" or "cooldown");
        var key = (peer.m_uid, stage, source ?? ZDOID.None, animal ?? ZDOID.None);
        float now = _clock();
        if (routine && _sourcePollTimes.TryGetValue(peer, out float last) && now >= last && now - last < 60) return false;
        if (!transition && _gates.TryGetValue(key, out var previous) && previous.Reason == reason && now >= previous.Time && now - previous.Time < 300) return false;
        if (routine)
        {
            if (_sourcePollTimes.Count >= 64 && !_sourcePollTimes.ContainsKey(peer)) _sourcePollTimes.Clear();
            _sourcePollTimes[peer] = now;
            if (!_traversal.Reserve(now, "source_poll", 2)) return false;
        }
        else if (!TravelSample(stage == "placement" ? "placement_waits" : "traversal")) return false;
        if (!transition)
        {
            if (_gates.Count >= 256 && !_gates.ContainsKey(key)) _gates.Clear();
            _gates[key] = (reason, now);
        }
        return true;
    }
    public void Traversal(ZNetPeer peer, string stage, string reason, ZDOID? source = null, ZDOID? animal = null,
        (string, object?)[]? fields = null, bool transition = false, bool ready = false, (string, object?)[]? verboseFields = null)
    {
        if (!ready && !TraversalReady(peer, stage, reason, source, animal, transition)) return;
        string message = reason == "no_portal_nearby" ? $"No portal near {peer.m_playerName}; travel not started." :
            $"Portal travel for {peer.m_playerName}: {Reason(reason)}.";
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel, message,
            new (string, object?)[] { ("pos", Evidence(fields, "pos")), ("followers", Evidence(fields, "followers")),
                ("stage", stage), ("peer", peer.m_uid), ("source", source), ("animal", animal), ("omitted", _traversal.TakeOmitted()) }, LogLevel.Debug);
        if (VerboseEnabled && (verboseFields != null || fields != null))
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel, $"Travel evidence for {peer.m_playerName}.", verboseFields ?? fields, LogLevel.Debug, verbose: true);
    }
    private static object? Evidence((string Key, object? Value)[]? fields, string key)
    { if (fields != null) foreach (var field in fields) if (field.Key == key) return field.Value; return null; }
    private static string Reason(string? reason) => reason switch
    {
        "character_missing" => "character not spawned yet",
        "destination_owner_confirmed" => "destination owner confirmed",
        null or "" or "unknown" => "outcome unavailable",
        _ => reason.Replace('_', ' ')
    };
    public void PortalPair(ZNetPeer peer, ZDO source, ZDO? destination, string reason)
    {
        if (!TraversalReady(peer, "portal_pair", reason, source.m_uid)) return;
        (string, object?)[]? evidence = null;
        if (VerboseAfterNormalEnabled)
        {
            TameTravelRules.Scale(source, out var scale);
            evidence = new (string, object?)[] { ("source_target", source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)),
                ("source_tag", source.GetString(ZDOVars.s_tag)), ("source_scale", scale), ("destination", destination?.m_uid),
                ("destination_target", destination?.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)),
                ("destination_tag", destination?.GetString(ZDOVars.s_tag)), ("destination_creator", destination?.GetLong(ZDOVars.s_creator)),
                ("separation_m", destination == null ? (float?)null : UnityEngine.Vector3.Distance(source.GetPosition(), destination.GetPosition())) };
        }
        Traversal(peer, "portal_pair", reason, source.m_uid, ready: true, verboseFields: evidence);
    }
    public void Command(ZNetPeer peer, string stage, string? command, string? reason = null, int? counter = null, ZDOID? animal = null)
    {
        if (!DiagnosticsEnabled || !_commands.Reserve(_clock(), "commands", 32)) return;
        string message = CommandMessage(peer.m_playerName, stage, command, reason);
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command, message,
            new (string, object?)[] { ("peer", peer.m_uid), ("character", peer.m_characterID), ("stage", stage), ("command", command),
                ("reason", reason), ("emote_counter", counter), ("animal", animal) }, LogLevel.Debug);
    }
    internal static string CommandMessage(string player, string stage, string? command, string? reason)
    {
        if (stage == "player_data_applied") return $"Observed fresh player data for {player}.";
        if (stage == "stop_queued" || stage == "reclaim_queued") return $"Queued {player}'s {command ?? "animal"} command — {Reason(reason ?? "pending")}.";
        if (stage == "actor_gate") return reason == "ready" ? $"{player}'s character is ready for animal commands." : $"Animal commands unavailable for {player} — {Reason(reason ?? "unknown")}.";
        if (reason != null) return $"Ignored {player}'s command — {Reason(reason)}.";
        return $"Received {player}'s {(string.IsNullOrEmpty(command) ? "animal" : command)} command.";
    }
    public void ActorGate(ZNetPeer peer, string? reason)
    {
        if (!DiagnosticsEnabled) return;
        if (_actorReasons.TryGetValue(peer.m_uid, out var previous) && previous == reason) return;
        if (_actorReasons.Count >= 64 && !_actorReasons.ContainsKey(peer.m_uid)) return;
        _actorReasons[peer.m_uid] = reason; Command(peer, "actor_gate", null, reason ?? "ready");
    }
    public void Selection(ZNetPeer peer, TameOrder order, int records, int supported, int eligible)
    {
        if (!DiagnosticsEnabled || !_selection.Reserve(_clock(), "selection", 8)) return;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command, $"Found {eligible} eligible animals for {peer.m_playerName}'s {order} command this pass.",
            new (string, object?)[] { ("peer", peer.m_uid), ("command", order), ("records", records), ("supported", supported), ("eligible", eligible) }, LogLevel.Debug);
    }
    public void Candidate(ZNetPeer peer, TameOrder order, ZDO animal, float distance, float range, string reason)
    {
        if (!DiagnosticsEnabled || !_selection.Reserve(_clock(), "selection", 8)) return;
        bool saved = animal.GetFloat(ZDOVars.s_health, out float health);
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command, $"Animal {animal.m_uid} for {peer.m_playerName}: {Reason(reason)}.",
            new (string, object?)[] { ("peer", peer.m_uid), ("command", order), ("animal", animal.m_uid), ("prefab", animal.GetPrefab()),
                ("distance_m", distance), ("range_m", range), ("health", saved ? health : (float?)null), ("owner", animal.GetOwner()) }, LogLevel.Debug);
        if (VerboseEnabled) DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Command, $"Selection evidence for animal {animal.m_uid}.",
            new LogField[] { DiagnosticLog.FieldAlways("health_saved", saved), DiagnosticLog.FieldAlways("tamed", animal.GetBool(ZDOVars.s_tamed)),
                ("following", animal.GetString(ZDOVars.s_follow)) }, LogLevel.Debug, verbose: true);
    }
    public void Entry(PortalJourney j, string mode = "owner_position_history")
    {
        if (!TravelSample()) return;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
            $"{j.Name} entered portal {j.Source} → {j.Destination} with {j.Followers.Count} {(j.Followers.Count == 1 ? "follower" : "followers")}.",
            new (string, object?)[] { ("exit", j.Exit), ("mode", mode == "owner_position_history" ? "position_history" : mode) }, LogLevel.Debug);
    }
    public void Transfer(AnimalTransfer t, bool confirmed, TransferSnapshot? snapshot)
    {
        if (!DiagnosticsEnabled || !_traversal.Reserve(_clock(), "transfer_end", 8)) return;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
            $"Transfer of animal {t.Id} for {t.Journey.Name} {(confirmed ? "finished" : "ended")} — {Reason(snapshot?.Reason)} after {t.PlacementAttempts} placement attempts ({t.PlacementCandidates} candidates).",
            new (string, object?)[] { ("search_ms", t.PlacementSearchMilliseconds), ("context_ms", t.PlacementContextMilliseconds) }, LogLevel.Debug);
        if (!VerboseEnabled) return;
        var forward = t.Journey.DestinationRotation * UnityEngine.Vector3.forward; forward.y = 0; forward = forward.normalized;
        var right = new UnityEngine.Vector3(forward.z, 0, -forward.x); var offset = t.Placement - t.Journey.DestinationPosition;
        // The same portal-facing frame measured from where the traveller stood when the landing was accepted.
        bool beside = t.Moved && t.PlacementTraveller.HasValue;
        var fromTraveller = beside ? t.Placement - t.PlacementTraveller!.Value : UnityEngine.Vector3.zero; fromTraveller.y = 0;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel, $"Transfer evidence for animal {t.Id}.",
            new (string, object?)[] { ("phase", t.Phase), ("moved", t.Moved), ("owner_observed", confirmed), ("reason", snapshot?.Reason),
                ("source_state_observed", t.SourceStateObserved), ("previous_owner", t.PreviousOwner), ("destination_retries", t.DestinationRetries),
                ("follow_continues", t.FollowContinues), ("rejections", PlacementReasons(t)), ("walkout_rejections", Reasons(t.PlacementWalkoutRejections)),
                ("wait_reasons", Reasons(t.PlacementWaitReasons)), ("surface_blocks", Reasons(t.PlacementSurfaceBlocks)),
                ("overlap_hits", Reasons(t.PlacementOverlapHits)), ("last_probe", t.PlacementLastProbe),
                ("support_y_m", t.PlacementSupportHeight), ("pos", t.Moved ? t.Placement : (UnityEngine.Vector3?)null),
                ("forward_m", t.Moved ? UnityEngine.Vector3.Dot(offset, forward) : (float?)null),
                ("side_m", t.Moved ? UnityEngine.Vector3.Dot(offset, right) : (float?)null),
                ("traveller_pos", beside ? t.PlacementTraveller : null),
                ("traveller_forward_m", beside ? DiagnosticLog.Kept(UnityEngine.Vector3.Dot(fromTraveller, forward)) : null),
                ("traveller_side_m", beside ? DiagnosticLog.Kept(UnityEngine.Vector3.Dot(fromTraveller, right)) : null),
                ("traveller_distance_m", beside ? fromTraveller.magnitude : (float?)null), ("same_record", snapshot?.SameRecord),
                ("owner", snapshot?.Owner), ("data_rev", snapshot?.DataRevision), ("owner_rev", snapshot?.OwnerRevision),
                ("position_before_release", snapshot?.Position), ("follow", snapshot?.Follow), ("health", snapshot?.Health),
                ("health_saved", snapshot?.HealthSaved), ("tamed", snapshot?.Tamed), ("rider", snapshot?.Rider),
                ("phase_age_s", snapshot?.PhaseAge), ("barrier_echoed", snapshot?.BarrierEchoed) }, LogLevel.Debug, verbose: true);
    }
    internal static string PlacementReasons(AnimalTransfer t) => Reasons(t.PlacementRejections);
    internal static string Reasons(System.Collections.Generic.Dictionary<string, int> counts) => string.Join(",", counts.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
    public void Cancel(PortalJourney j, string reason, UnityEngine.Vector3? position = null, float? now = null)
    {
        if (!TravelSample()) return;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel, $"Cancelled portal travel for {j.Name} — {Reason(reason)}.",
            new (string, object?)[] { ("source", j.Source), ("destination", j.Destination), ("pos", position), ("exit", j.Exit),
                ("distance_m", position.HasValue ? (position.Value - j.Exit).magnitude : (float?)null),
                ("maximum_m", TameTravelTraversalRules.DestinationLimit(j.Moved > 0)), ("entry_age_s", now - j.Entered),
                ("arrival_age_s", j.Arrived >= 0 ? now - j.Arrived : null) }, LogLevel.Debug);
    }
    public void Following(AnimalTransfer t, string state, float distance, float stop, float resume)
    {
        if (!Sample()) return;
        bool valid = t.Record != null && t.Record.IsValid() && t.Record.m_uid == t.Id;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Follow, $"Follow spacing for {t.Journey.Name}'s animal {t.Id}: {Reason(state)}.",
            new (string, object?)[] { ("animal", t.Id), ("phase", t.Phase), ("distance_m", distance), ("stop_m", stop), ("resume_m", resume),
                ("owner", valid ? t.Record!.GetOwner() : (long?)null), ("previous_owner", t.PreviousOwner), ("source_state_observed", t.SourceStateObserved) }, LogLevel.Debug);
    }
    // Ends are transitions, so they share the traversal allowance instead of the two-per-minute follow sample.
    public void FollowingEnded(AnimalTransfer t, string reason)
    {
        if (!TravelSample()) return;
        bool valid = t.Record != null && t.Record.IsValid() && t.Record.m_uid == t.Id;
        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Follow, $"Follow spacing for {t.Journey.Name}'s animal {t.Id} ended: {Reason(reason)}.",
            new (string, object?)[] { ("animal", t.Id), ("reason", reason), ("phase", t.Phase), ("peer", t.Journey.Peer.m_uid),
                ("owner", valid ? t.Record!.GetOwner() : (long?)null), ("omitted", _traversal.TakeOmitted()) }, LogLevel.Debug);
    }
    public long BeginFollowing() => DiagnosticsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    public void EndFollowing(long started, int scope)
    {
        if (started == 0 || !DiagnosticsEnabled) return;
        double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        _followCalls++; _followTotal += elapsed; _followMaximum = System.Math.Max(_followMaximum, elapsed);
        float now = _clock(); if (!_followNext.Ready(now)) return; _followNext.Postpone(now, 60);
        DiagnosticLog.Write(_log, "TameTravel", CoreLogTopics.Timing,
            $"{_followCalls} follow updates in the last minute, avg {(_followTotal / _followCalls).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} ms, max {_followMaximum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} ms.",
            new (string, object?)[] { ("scope", scope) }, LogLevel.Debug);
        _followCalls = 0; _followTotal = _followMaximum = 0;
    }
    public void Observe(float now, int bindings, int reserved, int transfers)
    {
        var current = (bindings, reserved, transfers);
        if (current == _last || (reserved == 0 && transfers == 0) || !Sample()) return;
        _last = current;
        DiagnosticLog.Write(_log, "TameTravel", CoreLogTopics.Scope, "Portal processing scope changed this pass.",
            new (string, object?)[] { ("bindings", bindings), ("reservations", reserved), ("transfers", transfers) }, LogLevel.Debug);
    }
}
