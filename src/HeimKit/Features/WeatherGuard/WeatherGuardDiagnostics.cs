using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Configuration;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly Func<bool> _debugEnabled;
    private readonly DebugLogLevel _level;
    private readonly HashSet<string> _wearPhases = new();
    public const int MediumSampleLimit = 12;
    internal bool LogWorldCreatedItems { get; }
    private readonly DiagnosticBudget _budget = new();
    private readonly Dictionary<string, (float Start, HashSet<string> Ids)> _wearStreams = new();
    private WeatherGuardStatus? _previous;
    private readonly Dictionary<RainEvidence, (float Health, float Lost, long Bypasses, long Gates)> _reported = new();
    private (float Health, float Lost, long Bypasses, long Gates) Previous(RainEvidence watch)
        => _reported.TryGetValue(watch, out var previous) ? previous : (watch.FirstHealth, 0, 0, 0);
    private float _lastSummary;
    private bool _capacityLimited;
    public const int SampleLimit = 6;
    public const int WearSampleLimit = 3;
    private bool Enabled => _debugEnabled() && DiagnosticLog.ServerActivity.Allowed;
    internal bool StepsEnabled => Enabled;
    internal bool DebugEnabled => Enabled && _level == DebugLogLevel.Detailed;
    public WeatherGuardDiagnostics(ManualLogSource logger, bool debug, DebugLogLevel level = DebugLogLevel.Detailed, bool logWorldCreated = true)
        : this(logger, () => debug, level, logWorldCreated) { }
    public WeatherGuardDiagnostics(ManualLogSource logger, DebugLoggingPolicy policy, DebugLogLevel level, bool logWorldCreated)
        : this(logger, () => policy.Enabled, level, logWorldCreated) { }
    private WeatherGuardDiagnostics(ManualLogSource logger, Func<bool> debugEnabled, DebugLogLevel level, bool logWorldCreated)
    { _logger = logger; _debugEnabled = debugEnabled; _level = level; LogWorldCreatedItems = logWorldCreated; }
    private void Write(DiagnosticEvent category, string result, Dictionary<string, object?> fields, LogLevel level = LogLevel.Debug)
        => DiagnosticLog.Write(_logger, "WeatherGuard", category, result, fields.Select(p => (p.Key, p.Value)), level);
    public static void StartupSettings(ManualLogSource logger, WeatherGuardSettings settings)
    {
        if (!settings.DebugLogging.Enabled || settings.DebugLogLevel.Value != DebugLogLevel.Detailed) return;
        DiagnosticLog.Write(logger, "WeatherGuard", DiagnosticEvent.Detail, "Settings and processing limits", new (string, object?)[]
        {
            ("rain_protection_enabled", settings.ProtectRain.Value),
            ("underwater_protection_enabled", settings.ProtectUnderwater.Value),
            ("excluded_prefabs", settings.ExcludedPrefabs.Value),
            ("debug_log_level", settings.DebugLogLevel.Value),
            ("log_world_created_items", settings.LogWorldCreatedItems.Value),
            ("maximum_regions", WeatherGuardRules.MaximumRegions),
            ("maximum_objects", WeatherGuardRules.MaximumObjects),
            ("maximum_protected", WeatherGuardRules.MaximumProtected),
            ("roof_check_max_age_s", WeatherGuardExposureTracking.MaximumObservationAgeSeconds),
            ("roof_probe_limit_per_scan", WeatherGuardExposureTracking.ProbeLimitPerScan),
            ("native_wet_timer_threshold_game_s", 60),
            ("native_weather_damage_percent", 5),
            ("scope", "eligible_player_built_pieces_in_retained_regions"),
            ("wetness_source", "server_environment_not_player_local_weather")
        }, LogLevel.Debug);
    }
    public void CapacityState(bool limited)
    {
        if (limited == _capacityLimited) return;
        _capacityLimited = limited;
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle,
            limited ? "Processing capacity reached. Unmanaged pieces retain vanilla processing; see debug status."
                : "Processing capacity limit cleared.", level: limited ? LogLevel.Warning : LogLevel.Info);
    }
    public void WeatherDamageSuppressed(int count, string? singleId = null, string? singlePrefab = null)
    {
        if (count <= 0) return;
        string target = count != 1 ? count + " pieces" : !string.IsNullOrEmpty(singleId)
            ? !string.IsNullOrEmpty(singlePrefab) ? singlePrefab + " (piece " + singleId + ")" : "piece " + singleId
            : "1 piece";
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Result,
            "Prevented weather damage to " + target + ".");
    }
    public void InitialAcquisition(int protectedCount)
    {
        if (!Enabled) return;
        Write(DiagnosticEvent.Detail, "Initial protection acquisition", new Dictionary<string, object?> { ["protected_now"] = protectedCount });
    }
    private static string Counts(Dictionary<string, int> counts)
        => string.Join(",", counts.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
    private static string ReleaseChanges(Dictionary<string, int> current, Dictionary<string, int> previous)
    {
        var changes = new Dictionary<string, int>();
        foreach (var reason in current)
        {
            previous.TryGetValue(reason.Key, out int before);
            int count = Math.Max(0, reason.Value - before);
            if (count > 0) changes[reason.Key] = count;
        }
        return Counts(changes);
    }
    private static long Delta(long current, long previous) => Math.Max(0, current - previous);
    public void Report(float now, Func<WeatherGuardStatus> snapshot, Func<IEnumerable<RainEvidence>>? rain = null, Func<IEnumerable<ProtectionObservation>>? protection = null, Func<Dictionary<string, object?>>? exposure = null)
    {
        if (!Enabled || !_budget.Reserve(now, "SUMMARY", 1, false)) return;
        var s = snapshot(); var old = _previous ?? new WeatherGuardStatus();
        var steps = new Dictionary<string, object?>
        {
            ["eligible_now"] = s.Eligible, ["managed_now"] = s.Protected, ["waiting_now"] = s.Waiting,
            ["wear_calls_interval"] = Delta(s.WearPasses, old.WearPasses),
            ["native_weather_due_interval"] = Delta(s.WeatherGates, old.WeatherGates),
            ["weather_contributions_removed_interval"] = Delta(s.WeatherSuppressed, old.WeatherSuppressed),
            ["confirmed_preventions_interval"] = Delta(s.ConfirmedPreventions, old.ConfirmedPreventions)
        };
        foreach (var decision in s.Decisions)
        {
            old.Decisions.TryGetValue(decision.Key, out long previous);
            steps[decision.Key + "_interval"] = Delta(decision.Value, previous);
        }
        if (exposure != null)
            foreach (var field in exposure()) steps[field.Key] = field.Value;
        if (_level == DebugLogLevel.Detailed)
        {
            steps["waiting_terrain_now"] = s.WaitingTerrain; steps["waiting_instance_now"] = s.WaitingInstance;
            steps["waiting_objects_now"] = s.WaitingObjects; steps["ready_players_now"] = s.ReadyPeers;
            steps["waiting_character_now"] = s.CharacterWaits; steps["exclusions_now"] = Counts(s.Exclusions);
            steps["capacity_limited"] = s.Limited; steps["omitted_messages_interval"] = _budget.TakeOmitted();
        }
        Write(DiagnosticEvent.Summary, "Protection path: eligible pieces, ownership, native weather conditions, wet timer, prevention", steps);
        if (_level == DebugLogLevel.Medium)
        { _previous = s; _lastSummary = now; return; }
        var watches = rain?.Invoke().Take(6).ToArray() ?? Array.Empty<RainEvidence>();
        // One reserved counter detail preserves networking/support evidence without
        // competing with object-loading and wear-detail samples.
        Write(DiagnosticEvent.Detail, "Processing and network counters", new()
        {
            ["interval_s"] = _previous == null ? 0 : Math.Max(0, now - _lastSummary),
            ["acquired_interval"] = Delta(s.Acquired, old.Acquired), ["released_interval"] = Delta(s.Released, old.Released),
            ["release_reasons_interval"] = ReleaseChanges(s.ReleaseTotals, old.ReleaseTotals),
            ["support_passes_interval"] = Delta(s.SupportPasses, old.SupportPasses), ["native_commands_interval"] = Delta(s.NativeCommands, old.NativeCommands),
            ["blocked_client_records_interval"] = Delta(s.BlockedData, old.BlockedData), ["prevented_transfers_interval"] = Delta(s.BlockedOwner, old.BlockedOwner),
            ["blocked_broadcasts_interval"] = Delta(s.BlockedBroadcast, old.BlockedBroadcast), ["routed_commands_interval"] = Delta(s.Commands, old.Commands),
            ["owner_losses_interval"] = Delta(s.OwnerLosses, old.OwnerLosses), ["release_reasons_total"] = Counts(s.ReleaseTotals),
            ["acquired_total"] = s.Acquired, ["released_total"] = s.Released, ["native_weather_due_total"] = s.WeatherGates,
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
        else if (!Flag(fields, "environment_wet") && !Flag(fields, "underwater")) reasons.Add("server_environment_dry");
        if (Flag(fields, "cached_roof") && !Flag(fields, "underwater")) reasons.Add((Flag(fields, "roof_cache_disagrees") || fields.TryGetValue("fresh_roof", out var freshRoof) && freshRoof is bool fresh && !fresh) ? "native_roof_cache_disagrees_with_geometry" : "covered");
        if (Flag(fields, "shielded")) reasons.Add("shielded");
        if (fields.ContainsKey("health_above_half") && !Flag(fields, "health_above_half")) reasons.Add("health_at_or_below_half");
        if (fields.TryGetValue("native_health_fraction", out var health) && health is float fraction && fraction <= 0.5f) reasons.Add("health_at_or_below_half");
        if (fields.ContainsKey("removable") && !Flag(fields, "removable")) reasons.Add("not_removable");
        if (Flag(fields, "no_building_fall")) reasons.Add("no_building_fall");
        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }
    private void Rain(float now, RainEvidence watch)
    {
        var previous = Previous(watch);
        if (watch.HealthLost <= previous.Lost) return;
        var fields = new Dictionary<string, object?>(watch.Identity)
        {
            ["managed"] = watch.Protected, ["eligibility"] = watch.Eligibility,
            ["health_before"] = previous.Health, ["health_after"] = watch.Health,
            ["health_lost_interval"] = watch.HealthLost - previous.Lost, ["health_minimum"] = watch.MinimumHealth,
            ["weather_cause"] = "unconfirmed",
            ["weather_contributions_due_interval"] = Delta(watch.Gates, previous.Gates),
            ["weather_contributions_removed_interval"] = Delta(watch.WeatherBypasses, previous.Bypasses),
            ["owner_changes_observed_total"] = watch.OwnerChanges,
            ["management_changes_observed_total"] = watch.ProtectionChanges
        };
        Write(DiagnosticEvent.Result, "Health loss observed; weather cause unconfirmed", fields);
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
        string phase = fields["blockers"] + ":" + observation.GateReached + ":" + observation.Suppressed;
        bool healthLoss = after.HasValue && after.Value < observation.Before;
        if (!failed && !healthLoss && !_wearPhases.Add(phase)) return;
        fields["condition_source"] = "server_instance";
        fields.TryGetValue("eligibility", out var reason);
        fields["protection"] = observation.Protected ? "active" : Equals(reason, "eligible") ? "waiting" : "excluded";
        fields["protection_reason"] = observation.Protected ? "eligible" : Equals(reason, "eligible") ? "not_acquired" : reason;
        fields["action"] = observation.Suppressed ? "weather_contribution_suppressed" : !observation.Owner ? "left_to_native_owner" :
            !observation.GateReached ? "weather_contribution_not_due" : "native_weather_contribution_allowed";
        fields["health_change"] = !after.HasValue ? "unavailable" : after.Value < observation.Before ? "loss_observed" : after.Value > observation.Before ? "increase_observed" : "unchanged";
        fields["weather_damage"] = "unconfirmed"; fields["damage_source"] = "unobserved";
        fields["managed"] = observation.Protected; fields["warmup"] = observation.Warmup;
        fields["native_weather_contribution_due"] = observation.GateReached; fields["weather_contribution_suppressed"] = observation.Suppressed;
        fields["health_before"] = observation.Before; fields["health_after"] = after;
        if (failed) fields["exception"] = true;
        Write(DiagnosticEvent.Detail, !observation.Owner ? "Wear calculation runs on another owner or is unassigned" : observation.Warmup ?
            "Waiting for native loading warmup" : !observation.GateReached ? "Native weather contribution was not due" :
            "Native weather contribution reached after native wet timer; condition details captured", fields, failed ? LogLevel.Error : LogLevel.Debug);
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
    public void Reset() { _capacityLimited = false; _budget.Reset(); _wearStreams.Clear(); _previous = null; _reported.Clear(); _wearPhases.Clear(); _lastSummary = 0; }
}
