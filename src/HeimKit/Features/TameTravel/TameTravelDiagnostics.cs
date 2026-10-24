using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.TameTravel.Models;
using HeimKit.Shared;
using System.Linq;
namespace HeimKit.Features.TameTravel;
internal sealed class TameTravelDiagnostics
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
    public bool SelectionEnabled => _policy.Enabled;
    private readonly System.Collections.Generic.Dictionary<long, string?> _actorReasons = new();
    private float _next;
    private int _samples;
    private int _followCalls;
    private double _followTotal, _followMaximum;
    private float _followNext;
    private (int Bindings, int Reserved, int Transfers) _last;
    public TameTravelDiagnostics(ManualLogSource log, DebugLoggingPolicy policy, System.Func<float>? clock = null)
    { _log = log; _policy = policy; _timing = new(log, "TameTravel", policy); _clock = clock ?? (() => UnityEngine.Time.unscaledTime); }
    private bool Sample()
    {
        if (!_policy.Enabled) return false;
        float now = _clock();
        if (now >= _next) { _next = now + 60; _samples = 0; }
        return _samples++ < 2;
    }
    private bool TravelSample() => _policy.Enabled && _traversal.Reserve(_clock(), "traversal", 6);
    public void Traversal(ZNetPeer peer, string stage, string reason, ZDOID? source = null, ZDOID? animal = null,
        (string, object?)[]? fields = null, bool transition = false)
    {
        if (!_policy.Enabled) return;
        bool routine = (stage is "source_entry" or "source_observation") &&
            (reason is "outside_trigger" or "no_portal_nearby" or "awaiting_owner_position" or "source_sample_gap" or "cooldown");
        var key = (peer.m_uid, stage, source ?? ZDOID.None, animal ?? ZDOID.None);
        float now = _clock();
        if (routine && _sourcePollTimes.TryGetValue(peer, out float last) && now >= last && now - last < 60) return;
        if (!transition && _gates.TryGetValue(key, out var previous) && previous.Reason == reason && now >= previous.Time && now - previous.Time < 300) return;
        if (routine)
        {
            if (_sourcePollTimes.Count >= 64 && !_sourcePollTimes.ContainsKey(peer)) _sourcePollTimes.Clear();
            _sourcePollTimes[peer] = now;
            if (!_traversal.Reserve(now, "source_poll", 2)) return;
        }
        else if (!TravelSample()) return;
        if (!transition)
        {
            if (_gates.Count >= 256 && !_gates.ContainsKey(key)) _gates.Clear();
            _gates[key] = (reason, now);
        }
        var evidence = new System.Collections.Generic.List<(string, object?)> { ("player", peer.m_playerName), ("peer", peer.m_uid),
            ("stage", stage), ("reason", reason), ("source", source), ("animal", animal), ("omitted", _traversal.TakeOmitted()) };
        if (fields != null) evidence.AddRange(fields);
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Portal traversal evidence.", evidence, LogLevel.Debug);
    }
    public void PortalPair(ZNetPeer peer, ZDO source, ZDO? destination, string reason)
    {
        if (!_policy.Enabled) return;
        TameTravelRules.Scale(source, out var scale);
        Traversal(peer, "portal_pair", reason, source.m_uid, fields: new (string, object?)[] {
            ("source_target", source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)), ("source_tag", source.GetString(ZDOVars.s_tag)),
            ("source_scale", scale), ("destination", destination?.m_uid),
            ("destination_target", destination?.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)),
            ("destination_tag", destination?.GetString(ZDOVars.s_tag)), ("destination_creator", destination?.GetLong(ZDOVars.s_creator)),
            ("separation_m", destination == null ? (float?)null : UnityEngine.Vector3.Distance(source.GetPosition(), destination.GetPosition())) });
    }
    public void Command(ZNetPeer peer, string stage, string? command, string? reason = null, int? counter = null, ZDOID? animal = null)
    {
        if (!_policy.Enabled || !_commands.Reserve(_clock(), "commands", 32)) return;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Animal command evidence.",
            new (string, object?)[] { ("player", peer.m_playerName), ("peer", peer.m_uid), ("character", peer.m_characterID),
                ("stage", stage), ("command", command), ("reason", reason), ("emote_counter", counter), ("animal", animal) }, LogLevel.Debug);
    }
    public void ActorGate(ZNetPeer peer, string? reason)
    {
        if (!_policy.Enabled) return;
        if (_actorReasons.TryGetValue(peer.m_uid, out var previous) && previous == reason) return;
        if (_actorReasons.Count >= 64 && !_actorReasons.ContainsKey(peer.m_uid)) return;
        _actorReasons[peer.m_uid] = reason;
        Command(peer, "actor_gate", null, reason ?? "ready");
    }
    public void Selection(ZNetPeer peer, TameOrder order, int records, int supported, int eligible)
    {
        if (!_policy.Enabled || !_selection.Reserve(_clock(), "selection", 8)) return;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Animal selection scope.",
            new (string, object?)[] { ("player", peer.m_playerName), ("peer", peer.m_uid), ("command", order),
                ("records_now", records), ("supported_now", supported), ("eligible_now", eligible) }, LogLevel.Debug);
    }
    public void Candidate(ZNetPeer peer, TameOrder order, ZDO animal, float distance, float range, string reason)
    {
        if (!_policy.Enabled || !_selection.Reserve(_clock(), "selection", 8)) return;
        bool saved = animal.GetFloat(ZDOVars.s_health, out float health);
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Animal selection candidate.",
            new (string, object?)[] { ("player", peer.m_playerName), ("peer", peer.m_uid), ("command", order), ("animal", animal.m_uid),
                ("prefab", animal.GetPrefab()), ("reason", reason), ("distance_m", distance), ("range_m", range),
                ("health_saved", saved), ("saved_health", saved ? health : (float?)null), ("tamed", animal.GetBool(ZDOVars.s_tamed)),
                ("following", animal.GetString(ZDOVars.s_follow)), ("owner", animal.GetOwner()) }, LogLevel.Debug);
    }
    public void Entry(PortalJourney j, string mode = "owner_position_history")
    {
        if (!TravelSample()) return;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Observed source portal entry.",
            new (string, object?)[] { ("player", j.Name), ("source", j.Source), ("destination", j.Destination), ("followers", j.Followers.Count),
                ("observation_mode", mode), ("source_position", j.SourcePosition), ("expected_exit", j.Exit) }, LogLevel.Debug);
    }
    public void Transfer(AnimalTransfer t, bool confirmed, TransferSnapshot? snapshot)
    {
        // Endings retain their own bounded allowance so repeated placement gates
        // cannot consume the evidence explaining the final outcome.
        if (!_policy.Enabled || !_traversal.Reserve(_clock(), "transfer_end", 8)) return;
        var forward = t.Journey.DestinationRotation * UnityEngine.Vector3.forward;
        forward.y = 0; forward = forward.normalized;
        var right = new UnityEngine.Vector3(forward.z, 0, -forward.x);
        var placementOffset = t.Placement - t.Journey.DestinationPosition;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Animal reservation ended.",
            new (string, object?)[] { ("player", t.Journey.Name), ("animal", t.Id), ("phase", t.Phase), ("moved", t.Moved), ("owner_observed", confirmed),
                ("reason", snapshot?.Reason), ("source_state_observed", t.SourceStateObserved), ("previous_owner", t.PreviousOwner),
                ("destination_retries", t.DestinationRetries),
                ("follow_continues", t.FollowContinues),
                ("placement_attempts", t.PlacementAttempts), ("placement_candidates", t.PlacementCandidates),
                ("placement_rejections", PlacementReasons(t)), ("placement_last_probe", t.PlacementLastProbe),
                ("placement_support_y", t.PlacementSupportHeight), ("placement_context_ms", t.PlacementContextMilliseconds),
                ("placement_search_ms", t.PlacementSearchMilliseconds), ("placement_position", t.Moved ? t.Placement : (UnityEngine.Vector3?)null),
                ("placement_forward_m", t.Moved ? UnityEngine.Vector3.Dot(placementOffset, forward) : (float?)null),
                ("placement_side_m", t.Moved ? UnityEngine.Vector3.Dot(placementOffset, right) : (float?)null),
                ("same_record", snapshot?.SameRecord), ("owner_before_release", snapshot?.Owner), ("data_revision_before_release", snapshot?.DataRevision),
                ("owner_revision_before_release", snapshot?.OwnerRevision), ("position_before_release", snapshot?.Position),
                ("follow_before_release", snapshot?.Follow), ("health_before_release", snapshot?.Health), ("health_saved", snapshot?.HealthSaved), ("tamed_before_release", snapshot?.Tamed),
                ("rider_before_release", snapshot?.Rider), ("phase_age_s", snapshot?.PhaseAge), ("barrier_echoed", snapshot?.BarrierEchoed) }, LogLevel.Debug);
    }
    internal static string PlacementReasons(AnimalTransfer t) => string.Join(",", t.PlacementRejections.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
    public void Cancel(PortalJourney j, string reason, UnityEngine.Vector3? position = null, float? now = null)
    {
        if (!TravelSample()) return;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Result, "Portal traversal cancelled.",
            new (string, object?)[] { ("player", j.Name), ("source", j.Source), ("destination", j.Destination), ("reason", reason),
                ("player_position", position), ("expected_exit", j.Exit), ("destination_distance_m", position.HasValue ? (position.Value - j.Exit).magnitude : (float?)null),
                ("maximum_distance_m", TameTravelTraversalRules.DestinationLimit(j.Moved > 0)), ("entry_age_s", now - j.Entered), ("arrival_age_s", j.Arrived >= 0 ? now - j.Arrived : null), ("ready", j.Ready) }, LogLevel.Debug);
    }
    public void Following(AnimalTransfer t, string state, float distance, float stop, float resume)
    {
        if (!Sample()) return;
        bool valid = t.Record != null && t.Record.IsValid() && t.Record.m_uid == t.Id;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Follower spacing state.",
            new (string, object?)[] { ("player", t.Journey.Name), ("animal", t.Id), ("state", state), ("phase", t.Phase),
                ("distance_xz_m", distance), ("stop_m", stop), ("resume_m", resume), ("owner", valid ? t.Record!.GetOwner() : (long?)null),
                ("previous_owner", t.PreviousOwner), ("source_state_observed", t.SourceStateObserved) }, LogLevel.Debug);
    }
    public long BeginFollowing() => _policy.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    public void EndFollowing(long started, int scope)
    {
        if (started == 0 || !_policy.Enabled) return;
        double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        _followCalls++; _followTotal += elapsed; _followMaximum = System.Math.Max(_followMaximum, elapsed);
        float now = _clock();
        if (now < _followNext) return;
        _followNext = now + 60;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Detail, "Follow movement timing; other native AI and physics are excluded.",
            new (string, object?)[] { ("followers_now", scope), ("calls_interval", _followCalls),
                ("duration_avg_ms", _followTotal / _followCalls), ("duration_max_ms", _followMaximum) }, LogLevel.Debug);
        _followCalls = 0; _followTotal = _followMaximum = 0;
    }
    public void Observe(float now, int bindings, int reserved, int transfers)
    {
        // Operational state lives in the controller; snapshots consume the same feature sample budget.
        var current = (bindings, reserved, transfers);
        if (current == _last || (reserved == 0 && transfers == 0) || !Sample()) return;
        _last = current;
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Summary, "Portal processing scope.",
            new (string, object?)[] { ("bindings_now", bindings), ("reservations_now", reserved), ("transfers_now", transfers) }, LogLevel.Debug);
    }
}
