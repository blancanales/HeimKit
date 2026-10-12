using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly bool _debug;
    private readonly DebugLogLevel _level;
    private readonly Dictionary<string, string> _mediumStates = new();
    private int _mediumCursor;
    public const int MediumSampleLimit = 12;
    internal bool LogWorldCreatedItems { get; }
    private readonly DiagnosticBudget _budget = new();
    private readonly Dictionary<string, (float Start, HashSet<string> Ids)> _wearStreams = new();
    private WeatherGuardStatus? _previous;
    private readonly Dictionary<RainEvidence, (float Health, float Lost, long Bypasses, long Gates)> _reported = new();
    private (float Health, float Lost, long Bypasses, long Gates) Previous(RainEvidence watch)
        => _reported.TryGetValue(watch, out var previous) ? previous : (watch.FirstHealth, 0, 0, 0);
    private float _lastSummary;
    public const int SampleLimit = 6;
    public const int WearSampleLimit = 3;
    internal bool DebugEnabled => _debug && DiagnosticLog.ServerActivity.Allowed && _level == DebugLogLevel.Detailed;
    public WeatherGuardDiagnostics(ManualLogSource logger, bool debug, DebugLogLevel level = DebugLogLevel.Detailed, bool logWorldCreated = true)
    { _logger = logger; _debug = debug; _level = level; LogWorldCreatedItems = logWorldCreated; }
    private void Write(DiagnosticEvent category, string result, Dictionary<string, object?> fields, LogLevel level = LogLevel.Debug)
        => DiagnosticLog.Write(_logger, "WeatherGuard", category, result, fields.Select(p => (p.Key, p.Value)), level);
    private static string Counts(Dictionary<string, int> counts)
        => string.Join(",", counts.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
    private static long Delta(long current, long previous) => Math.Max(0, current - previous);
    public void Report(float now, Func<WeatherGuardStatus> snapshot, Func<IEnumerable<RainEvidence>>? rain = null, Func<IEnumerable<ProtectionObservation>>? protection = null)
    {
        if (!_debug || !DiagnosticLog.ServerActivity.Allowed || !_budget.Reserve(now, "SUMMARY", 1, false)) return;
        if (_level == DebugLogLevel.Medium)
        { Medium(now, protection?.Invoke() ?? Array.Empty<ProtectionObservation>()); return; }
        var s = snapshot(); var old = _previous ?? new WeatherGuardStatus();
        var watches = rain?.Invoke().Take(6).ToArray() ?? Array.Empty<RainEvidence>();
        Write(DiagnosticEvent.Summary, !s.RainProtection && !s.UnderwaterProtection ? "Rain and underwater protection are both disabled" : s.Waiting > 0 ? "Some eligible pieces are waiting for context" : s.Eligible == 0 ? "Waiting for eligible player-built pieces" : "Eligible pieces are managed; rain evidence reported separately", new()
        {
            ["interval_s"] = _previous == null ? 0 : Math.Max(0, now - _lastSummary),
            ["rain_protection_enabled"] = s.RainProtection, ["underwater_protection_enabled"] = s.UnderwaterProtection,
            ["eligible_now"] = s.Eligible, ["managed_now"] = s.Protected,
            ["waiting_terrain_now"] = s.WaitingTerrain, ["waiting_instance_now"] = s.WaitingInstance,
            ["waiting_objects_now"] = s.WaitingObjects, ["ready_players_now"] = s.ReadyPeers, ["waiting_character_now"] = s.CharacterWaits,
            ["watched_player_pieces_now"] = watches.Count(w => w.Eligibility == "eligible"),
            ["watched_world_controls_now"] = watches.Count(w => w.Eligibility == "world-created"),
            ["watched_health_losses_interval"] = watches.Count(w => w.HealthLost > Previous(w).Lost),
            ["watched_weather_bypasses_interval"] = watches.Sum(w => Delta(w.WeatherBypasses, Previous(w).Bypasses)),
            ["wet_gates_interval"] = Delta(s.WetOpportunities, old.WetOpportunities),
            ["acquired_interval"] = Delta(s.Acquired, old.Acquired), ["released_interval"] = Delta(s.Released, old.Released),
            ["exclusions_now"] = Counts(s.Exclusions), ["capacity_limited"] = s.Limited,
            ["omitted_messages_interval"] = _budget.TakeOmitted()
        });
        // One reserved counter detail preserves networking/support evidence without
        // competing with object-loading and wear-detail samples.
        Write(DiagnosticEvent.Detail, "Processing and network counters", new()
        {
            ["interval_s"] = _previous == null ? 0 : Math.Max(0, now - _lastSummary),
            ["wear_calls_interval"] = Delta(s.WearPasses, old.WearPasses), ["weather_gates_interval"] = Delta(s.WeatherGates, old.WeatherGates),
            ["support_passes_interval"] = Delta(s.SupportPasses, old.SupportPasses), ["native_commands_interval"] = Delta(s.NativeCommands, old.NativeCommands),
            ["blocked_client_records_interval"] = Delta(s.BlockedData, old.BlockedData), ["prevented_transfers_interval"] = Delta(s.BlockedOwner, old.BlockedOwner),
            ["blocked_broadcasts_interval"] = Delta(s.BlockedBroadcast, old.BlockedBroadcast), ["routed_commands_interval"] = Delta(s.Commands, old.Commands),
            ["owner_losses_interval"] = Delta(s.OwnerLosses, old.OwnerLosses), ["release_reasons_total"] = Counts(s.ReleaseTotals),
            ["acquired_total"] = s.Acquired, ["released_total"] = s.Released, ["weather_gates_total"] = s.WeatherGates,
            ["regions_now"] = s.Regions, ["nearby_records_now"] = s.Nearby, ["distant_records_now"] = s.Distant
        });
        foreach (var watch in watches) Rain(now, watch);
        _reported.Clear();
        foreach (var watch in watches) _reported[watch] = (watch.Health, watch.HealthLost, watch.WeatherBypasses, watch.Gates);
        _previous = s; _lastSummary = now;
    }
    private static bool Flag(Dictionary<string, object?> fields, string key) => fields.TryGetValue(key, out var value) && value is bool flag && flag;
    private static string Blockers(Dictionary<string, object?> fields, bool owner, bool warmup)
    {
        var reasons = new List<string>();
        if (!owner) reasons.Add("not_server_processed");
        if (warmup) reasons.Add("warmup");
        if (!fields.ContainsKey("environment_wet")) reasons.Add("conditions_unknown");
        else if (!Flag(fields, "environment_wet") && !Flag(fields, "underwater")) reasons.Add("dry");
        if (Flag(fields, "cached_roof") && !Flag(fields, "underwater")) reasons.Add("covered");
        if (Flag(fields, "shielded")) reasons.Add("shielded");
        if (fields.ContainsKey("health_above_half") && !Flag(fields, "health_above_half")) reasons.Add("health_at_or_below_half");
        if (fields.TryGetValue("native_health_fraction", out var health) && health is float fraction && fraction <= 0.5f) reasons.Add("health_at_or_below_half");
        if (fields.ContainsKey("removable") && !Flag(fields, "removable")) reasons.Add("not_removable");
        if (Flag(fields, "no_building_fall")) reasons.Add("no_building_fall");
        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }
    private void Rain(float now, RainEvidence watch)
    {
        var fields = new Dictionary<string, object?>(watch.Identity);
        bool serverOwned = watch.Protected && watch.Fields.TryGetValue("processor", out var actualProcessor) && Equals(actualProcessor, "server");
        string blockers = Blockers(watch.Fields, serverOwned, Flag(watch.Fields, "warmup"));
        string result;
        var previous = Previous(watch);
        long bypasses = Delta(watch.WeatherBypasses, previous.Bypasses);
        fields["protection"] = watch.Protected ? "active" : watch.Eligibility == "eligible" ? "waiting" : "excluded";
        fields["protection_reason"] = watch.ProtectionReason;
        fields["health_change"] = watch.HealthLost > previous.Lost ? "loss_observed" : watch.Health > previous.Health ? "increase_observed" : "unchanged";
        fields["health_before"] = previous.Health; fields["health_after"] = watch.Health;
        fields["health_lost_interval"] = Math.Max(0, watch.HealthLost - previous.Lost);
        fields["health_loss_observations_total"] = watch.HealthLossObservations;
        fields["weather_damage"] = "unconfirmed";
        fields["damage_source"] = watch.HealthLossObservations > 0 ? "unobserved" : "no_health_loss_observed";
        fields["action"] = bypasses > 0 ? "weather_branch_bypassed" : !watch.Protected ? "left_to_native_owner" : "weather_bypass_not_observed";
        fields["weather_gates_interval"] = Delta(watch.Gates, previous.Gates);
        fields["weather_bypasses_interval"] = bypasses; fields["weather_bypasses_total"] = watch.WeatherBypasses;
        fields["action_scope"] = "reporting_interval";
        fields["weather_opportunity"] = !serverOwned ? "owner_calculation_unavailable" : blockers != "none" ? "conditions_not_met" :
            watch.WetSeconds > 60 ? "qualifying_duration_observed" : "qualifying_duration_not_reached";
        if (watch.HealthLost > previous.Lost) result = "Health loss observed; weather cause unconfirmed";
        else if (bypasses > 0) result = "Weather processing bypassed; no health loss observed in interval";
        else if (!watch.Protected) result = "Piece unprotected; native owner controls wear";
        else result = "Piece protected; weather bypass not observed in interval";
        fields["source"] = serverOwned ? "server_owned_state" : "replicated_state";
        fields["condition_source"] = "server_instance";
        fields["rain_protection"] = watch.Fields.TryGetValue("rain_protection", out var rainMode) ? rainMode : null;
        fields["underwater_protection"] = watch.Fields.TryGetValue("underwater_protection", out var waterMode) ? waterMode : null;
        fields["eligibility"] = watch.Eligibility; fields["managed"] = watch.Protected;
        fields["tracked_s"] = now - watch.Started; fields["blockers"] = blockers;
        fields["health_first"] = watch.FirstHealth; fields["health_current"] = watch.Health; fields["health_minimum"] = watch.MinimumHealth;
        fields["health_delta"] = watch.Health - watch.FirstHealth; fields["maximum_health"] = watch.MaximumHealth;
        fields["wet_observed_s"] = watch.Eligibility == "world-created" ? null : watch.WetSeconds;
        fields["longest_wet_observed_s"] = watch.Eligibility == "world-created" ? null : watch.LongestWetSeconds;
        fields["weather_gates_total"] = watch.Gates; fields["wet_gates_total"] = watch.WetGates;
        fields["creator_changed"] = watch.CreatorChanged;
        fields["processor"] = watch.Fields.TryGetValue("processor", out var processor) ? processor : null;
        if (Flag(watch.Fields, "warmup")) fields["warmup_remaining_s"] = watch.Fields.TryGetValue("warmup_remaining_s", out var remaining) ? remaining : null;
        fields["cached_roof"] = watch.Fields.TryGetValue("cached_roof", out var roof) ? roof : null;
        fields["environment_wet"] = watch.Fields.TryGetValue("environment_wet", out var wet) ? wet : null;
        if (watch.FreshRoof.HasValue && (!roof?.Equals(watch.FreshRoof.Value) ?? true))
        { fields["fresh_roof"] = watch.FreshRoof; fields["roof_probe_age_s"] = now - watch.FreshRoofAt; fields["cover_timer"] = watch.Fields.TryGetValue("cover_timer", out var timer) ? timer : null; }
        if (watch.OwnerChanges > 0 || watch.ProtectionChanges > 0)
        { fields["owner_changes_observed_total"] = watch.OwnerChanges; fields["management_changes_observed_total"] = watch.ProtectionChanges; }
        Write(DiagnosticEvent.Result, result, fields);
    }
    public void Sample(float now, Func<string> describe) => Sample(now, "acquisition", describe);
    public void Sample(float now, string stream, Func<string> describe)
        => Record(now, stream, () => (describe(), new Dictionary<string, object?>()));
    public void Record(float now, string stream, Func<(string Message, Dictionary<string, object?> Fields)> describe)
    {
        if (!DebugEnabled) return;
        var category = stream == "acquisition" || stream == "ownership" ? DiagnosticEvent.Action :
            stream.Contains("discovery") || stream == "eligibility" || stream == "rain ended" ? DiagnosticEvent.Object : DiagnosticEvent.Detail;
        if (!_budget.Reserve(now, category.ToString().ToUpperInvariant(), SampleLimit)) return;
        var payload = describe();
        var fields = payload.Fields;
        object? candidate = fields.TryGetValue("eligibility", out var eligibility) ? eligibility : fields.TryGetValue("reason", out var reason) ? reason : null;
        if ((stream.Contains("discovery") || stream == "eligibility") && candidate != null &&
            !Equals(candidate, "eligible") && !(LogWorldCreatedItems && Equals(candidate, "world-created"))) return;
        Write(category, payload.Message, fields);
    }
    public bool ReserveCommand(float now) => DebugEnabled && _budget.Reserve(now, "native_RESULT", SampleLimit);
    public void Command(WearObservation observation, float? after, bool failed)
    {
        if (!DebugEnabled && !failed) return;
        var fields = new Dictionary<string, object?>(observation.Identity);
        foreach (var field in observation.Fields) fields[field.Key] = field.Value;
        fields["health_before"] = observation.Before; fields["health_after"] = after;
        fields["object_available_after"] = after.HasValue;
        fields["protection"] = observation.Protected ? "active" : "unknown";
        fields["action"] = failed ? "native_command_failed" : "native_command_executed";
        fields["health_change"] = !after.HasValue ? "unavailable" : after.Value < observation.Before ? "loss_observed" : after.Value > observation.Before ? "increase_observed" : "unchanged";
        fields["weather_damage"] = "unconfirmed";
        fields["damage_context"] = "native_command";
        if (failed) fields["exception"] = true;
        Write(DiagnosticEvent.Result, failed ? "Native receiver raised an exception" : "Native receiver executed; health outcome observed", fields, failed ? LogLevel.Error : LogLevel.Debug);
    }
    public bool TryWearSample(float now, string id, bool protectedPiece, bool exposed = false)
    {
        if (!DebugEnabled) return false;
        string stream = $"{protectedPiece}:{exposed}";
        if (!_wearStreams.TryGetValue(stream, out var window) || now < window.Start || now - window.Start >= 60)
            window = (now, new HashSet<string>());
        if (window.Ids.Count >= WearSampleLimit || !window.Ids.Add(id)) return false;
        _wearStreams[stream] = window;
        return _budget.Reserve(now, "DETAIL", SampleLimit);
    }
    public void Wear(WearObservation observation, float? after, bool failed)
    {
        if (!DebugEnabled && !failed) return;
        var fields = new Dictionary<string, object?>(observation.Identity);
        foreach (var field in observation.Fields) fields[field.Key] = field.Value;
        fields["blockers"] = Blockers(observation.Fields, observation.Owner, observation.Warmup);
        fields["condition_source"] = "server_instance";
        fields.TryGetValue("eligibility", out var reason);
        fields["protection"] = observation.Protected ? "active" : Equals(reason, "eligible") ? "waiting" : "excluded";
        fields["protection_reason"] = observation.Protected ? "eligible" : Equals(reason, "eligible") ? "not_acquired" : reason;
        fields["action"] = observation.Suppressed ? "weather_branch_bypassed" : !observation.Owner ? "left_to_native_owner" :
            !observation.GateReached ? "weather_gate_not_reached" : "native_weather_branch_allowed";
        fields["health_change"] = !after.HasValue ? "unavailable" : after.Value < observation.Before ? "loss_observed" : after.Value > observation.Before ? "increase_observed" : "unchanged";
        fields["weather_damage"] = "unconfirmed"; fields["damage_source"] = "unobserved";
        fields["managed"] = observation.Protected; fields["warmup"] = observation.Warmup;
        fields["weather_gate_reached"] = observation.GateReached; fields["weather_branch_bypassed"] = observation.Suppressed;
        fields["health_before"] = observation.Before; fields["health_after"] = after;
        if (failed) fields["exception"] = true;
        Write(DiagnosticEvent.Detail, !observation.Owner ? "Wear calculation runs on another owner or is unassigned" : observation.Warmup ?
            "Waiting for native loading warmup" : !observation.GateReached ? "Native weather gate was not reached" :
            "Native weather gate reached; condition details captured", fields, failed ? LogLevel.Error : LogLevel.Debug);
    }
    private void Medium(float now, IEnumerable<ProtectionObservation> observations)
    {
        var items = observations.Where(item => item.Origin == "player_built" || LogWorldCreatedItems && item.Origin == "world_created")
            .Take(WeatherGuardRules.MaximumObjects).ToArray();
        var ids = new HashSet<string>(items.Select(item => Convert.ToString(item.Identity["object"])!));
        foreach (var id in _mediumStates.Keys.Where(id => !ids.Contains(id)).ToArray()) _mediumStates.Remove(id);
        string Key(ProtectionObservation item) => Convert.ToString(item.Identity["object"])!;
        string State(ProtectionObservation item) => $"{item.Protected}:{item.Reason}";
        var changed = items.Where(item => !_mediumStates.TryGetValue(Key(item), out var state) || state != State(item)).ToArray();
        var selected = changed.Take(MediumSampleLimit).ToList();
        var selectedIds = new HashSet<string>(selected.Select(Key));
        for (int visited = 0; visited < items.Length && selected.Count < MediumSampleLimit; visited++)
        {
            _mediumCursor %= items.Length;
            var item = items[_mediumCursor++];
            if (selectedIds.Add(Key(item))) selected.Add(item);
        }
        Write(DiagnosticEvent.Summary, "Qualifying structure protection status", new()
        {
            ["log_level"] = "Medium", ["qualifying_items_now"] = items.Length,
            ["protected_now"] = items.Count(item => item.Protected),
            ["rain_protected_now"] = items.Count(item => item.Protected && item.RainProtection),
            ["underwater_protected_now"] = items.Count(item => item.Protected && item.UnderwaterProtection),
            ["unprotected_player_items_now"] = items.Count(item => !item.Protected && item.Origin == "player_built"),
            ["unprotected_world_items_now"] = items.Count(item => !item.Protected && item.Origin == "world_created"),
            ["world_items_included"] = LogWorldCreatedItems, ["item_samples_now"] = selected.Count,
            ["pending_changed_samples_now"] = Math.Max(0, changed.Length - selected.Count)
        });
        foreach (var item in selected)
        {
            var fields = new Dictionary<string, object?>(item.Identity)
            { ["protected"] = item.Protected, ["rain_protected"] = item.Protected && item.RainProtection,
              ["underwater_protected"] = item.Protected && item.UnderwaterProtection, ["origin"] = item.Origin, ["reason"] = item.Reason };
            Write(DiagnosticEvent.Result, item.Protected ? "Item protected" : "Item not protected", fields);
            _mediumStates[Key(item)] = State(item);
        }
    }
    public void Coverage(CoverageSnapshot s)
    {
        string measured = s.Observed
            ? $"Eligible {s.Eligible}, protected {s.Protected}, waiting {s.Waiting}, skipped {s.Skipped}, unloaded eligible {s.UnloadedEligible}. " +
              $"Skip reasons: {Counts(s.SkipReasons)}. Wait reasons: {Counts(s.WaitReasons)}. "
            : "Eligible, protected, waiting and skipped counts are unknown. ";
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Summary,
            $"Requested coverage report ({s.State}): {measured}" +
            $"Scope: retained nearby ready-player regions; observed {s.Observed}; scan age {DiagnosticLog.Value(s.ScanAge)} seconds; " +
            $"regions {s.Regions}, nearby records {s.NearbyRecords}, distant records unevaluated {s.DistantRecords}; capacity limited {s.Limited}. " +
            $"Rain protection {s.RainEnabled}; underwater protection {s.UnderwaterEnabled}. " +
            "The rest of the world is unobserved; weather-damage prevention is not verified.");
    }
    public void Reset() { _budget.Reset(); _wearStreams.Clear(); _previous = null; _reported.Clear(); _mediumStates.Clear(); _mediumCursor = 0; _lastSummary = 0; }
}
