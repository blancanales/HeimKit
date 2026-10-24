using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardDiagnostics : FeatureDiagnostics, IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly HashSet<ZDOID> _prevented = new();
    private DateTime _windowStart = DiagnosticLog.WallClock();
    private long _preventionCount;
    private ZDOID _singlePiece;
    private string? _singlePrefab;
    private readonly Func<bool> _debugEnabled;
    private readonly DebugLogLevel _level;
    private readonly HashSet<string> _wearPhases = new();
    public const int MediumSampleLimit = 12;
    internal bool LogWorldCreatedItems { get; }
    private readonly Dictionary<string, (float Start, HashSet<string> Ids)> _wearStreams = new();
    private WeatherGuardStatus? _previous;
    private readonly Dictionary<RainEvidence, (float Health, float Lost, long Bypasses, long Gates)> _reported = new();
    private (float Health, float Lost, long Bypasses, long Gates) Previous(RainEvidence watch)
        => _reported.TryGetValue(watch, out var previous) ? previous : (watch.FirstHealth, 0, 0, 0);
    private float _lastSummary;
    private bool _capacityLimited;
    public const int SampleLimit = 6;
    public const int WearSampleLimit = 3;
    private bool Enabled => DiagnosticsEnabled;
    internal bool StepsEnabled => Enabled;
    internal bool DebugEnabled => Enabled && _level == DebugLogLevel.Detailed;
    public WeatherGuardDiagnostics(ManualLogSource logger, bool debug, DebugLogLevel level = DebugLogLevel.Detailed, bool logWorldCreated = true)
        : this(logger, () => debug, level, logWorldCreated) { }
    public WeatherGuardDiagnostics(ManualLogSource logger, DebugLoggingPolicy policy, DebugLogLevel level, bool logWorldCreated)
        : this(logger, () => policy.Enabled, level, logWorldCreated) { }
    private WeatherGuardDiagnostics(ManualLogSource logger, Func<bool> debugEnabled, DebugLogLevel level, bool logWorldCreated)
    { _logger = logger; _debugEnabled = debugEnabled; ConfigureDiagnostics(debugEnabled, logger, "WeatherGuard"); _level = level; LogWorldCreatedItems = logWorldCreated; }
    private void Write(LogTopic topic, string result, Dictionary<string, object?> fields, LogLevel level = LogLevel.Debug, Exception? error = null)
    {
        if (level == LogLevel.Debug && !DiagnosticsEnabled) return;
        LogFields evidence = fields;
        // Error records reserve three fields for the exception; Verbose continues where Normal stopped.
        int shown = error == null ? 8 : 5;
        DiagnosticLog.Write(_logger, "WeatherGuard", topic, result, evidence.Take(shown), level, error: error);
        if (VerboseEnabled && fields.Count > shown)
            DiagnosticLog.Write(_logger, "WeatherGuard", topic, Subject(fields, "Additional evidence"), evidence.Skip(shown), LogLevel.Debug, verbose: true);
    }
    // Verbose records name their piece so they stay readable when other records interleave.
    private static string Subject(Dictionary<string, object?> fields, string evidence)
    {
        object? id = fields.TryGetValue("object", out var value) ? value : null;
        object? prefab = fields.TryGetValue("prefab", out value) ? value : null;
        return id == null ? evidence + " for the preceding record." : prefab == null ? $"{evidence} for piece {id}." : $"{evidence} for {prefab} (piece {id}).";
    }
    public static void StartupSettings(ManualLogSource logger, WeatherGuardSettings settings)
    {
        if (!settings.DebugLogging.Enabled || settings.DebugLogLevel.Value != DebugLogLevel.Detailed) return;
        { if (DiagnosticLog.DebugAvailable(logger, "WeatherGuard")) DiagnosticLog.Write(logger, "WeatherGuard", CoreLogTopics.Config, "Loaded settings and processing limits.", new LogField[] { DiagnosticLog.FieldAlways("rain_protection_enabled", settings.ProtectRain.Value), DiagnosticLog.FieldAlways("underwater_protection_enabled", settings.ProtectUnderwater.Value), ("excluded_prefabs", settings.ExcludedPrefabs.Value), ("debug_log_level", settings.DebugLogLevel.Value), DiagnosticLog.FieldAlways("log_world_created_items", settings.LogWorldCreatedItems.Value), ("maximum_regions", WeatherGuardRules.MaximumRegions), ("maximum_objects", WeatherGuardRules.MaximumObjects), ("maximum_protected", WeatherGuardRules.MaximumProtected) }, LogLevel.Debug); if (DiagnosticLog.VerboseConfigured && DiagnosticLog.DebugAvailable(logger, "WeatherGuard", true)) DiagnosticLog.Write(logger, "WeatherGuard", CoreLogTopics.Config, "Additional evidence for the loaded settings and processing limits.", new (string, object?)[] { ("roof_check_max_age_s", WeatherGuardExposureTracking.MaximumObservationAgeSeconds), ("roof_probe_limit_per_scan", WeatherGuardExposureTracking.ProbeLimitPerScan), ("native_wet_timer_threshold_game_s", 60), ("native_weather_damage_percent", 5), ("scope", "eligible_player_built_pieces_in_retained_regions"), ("wetness_source", "server_environment_not_player_local_weather") }, LogLevel.Debug, verbose: true); }
    }
    public void CapacityState(bool limited)
    {
        if (limited == _capacityLimited) return;
        _capacityLimited = limited;
        DiagnosticLog.Write(_logger, "WeatherGuard", limited ? "Processing capacity reached. Unmanaged pieces retain vanilla processing; see debug status."
                : "Processing capacity limit cleared.", level: limited ? LogLevel.Warning : LogLevel.Info);
    }
    public void WeatherDamageSuppressed(ZDOID id, string? prefab = null)
    {
        PollPreventionRollup();
        if (_prevented.Contains(id)) return;
        if (_prevented.Count < WeatherGuardRules.MaximumProtected) _prevented.Add(id);
        if (_preventionCount++ == 0) { _singlePiece = id; _singlePrefab = prefab; }
    }
    public void PollPreventionRollup()
    {
        var now = DiagnosticLog.WallClock();
        if (now < _windowStart) { FlushPreventions(true); _windowStart = now; return; }
        if (now - _windowStart < TimeSpan.FromMinutes(10)) return;
        FlushPreventions(false);
        long windows = (now - _windowStart).Ticks / TimeSpan.FromMinutes(10).Ticks;
        _windowStart = _windowStart.AddTicks(windows * TimeSpan.FromMinutes(10).Ticks);
    }
    public void FlushPreventions(bool partial = true)
    {
        if (_preventionCount > 0)
        {
            string target = _preventionCount != 1 ? _preventionCount + " pieces" :
                !string.IsNullOrEmpty(_singlePrefab) ? _singlePrefab + " (piece " + _singlePiece + ")" : "piece " + _singlePiece;
            string window = partial ? " since " + _windowStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : " in the last 10 minutes";
            DiagnosticLog.Write(_logger, "WeatherGuard", "Prevented weather damage to " + target + window + ".");
        }
        _preventionCount = 0; _prevented.Clear(); _singlePiece = ZDOID.None; _singlePrefab = null;
    }
    public void InitialAcquisition(int protectedCount)
    {
        if (!Enabled) return;
        Write(WeatherGuardLogTopics.Protection, "Acquired initial protection for eligible pieces.", new Dictionary<string, object?> { ["protected"] = protectedCount });
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
        if (!Enabled || !_budget.Reserve(now, "summary", 1, false)) return;
        var s = snapshot(); var old = _previous ?? new WeatherGuardStatus();
        var steps = new Dictionary<string, object?>
        {
            ["eligible"] = s.Eligible, ["managed"] = s.Protected, ["waiting"] = s.Waiting,
            ["wear_calls"] = Delta(s.WearPasses, old.WearPasses),
            ["native_weather_due"] = Delta(s.WeatherGates, old.WeatherGates),
            ["weather_contributions_removed"] = Delta(s.WeatherSuppressed, old.WeatherSuppressed),
            ["confirmed_preventions"] = Delta(s.ConfirmedPreventions, old.ConfirmedPreventions)
        };
        Write(CoreLogTopics.Scope, "Observed the protection path in the last minute.", steps);
        if (VerboseEnabled)
        {
            var evidence = new Dictionary<string, object?>();
            foreach (var decision in s.Decisions)
            { old.Decisions.TryGetValue(decision.Key, out long previous); evidence[decision.Key] = Delta(decision.Value, previous); }
            if (exposure != null) foreach (var field in exposure()) evidence[field.Key] = field.Value;
            if (_level == DebugLogLevel.Detailed)
            {
                evidence["waiting_terrain"] = s.WaitingTerrain; evidence["waiting_instance"] = s.WaitingInstance;
                evidence["waiting_objects"] = s.WaitingObjects; evidence["ready_players"] = s.ReadyPeers;
                evidence["waiting_character"] = s.CharacterWaits; evidence["exclusions"] = Counts(s.Exclusions);
                evidence["capacity_limited"] = s.Limited; evidence["omitted_messages"] = _budget.TakeOmitted();
            }
            DiagnosticLog.Write(_logger, "WeatherGuard", CoreLogTopics.Scope, "Observed supporting protection evidence in the last minute.", evidence, LogLevel.Debug, verbose: true);
        }
        if (_level == DebugLogLevel.Medium)
        { _previous = s; _lastSummary = now; return; }
        var watches = rain?.Invoke().Take(6).ToArray() ?? Array.Empty<RainEvidence>();
        // One reserved counter detail preserves networking/support evidence without
        // competing with object-loading and wear-detail samples.
        if (Enabled) Write(WeatherGuardLogTopics.Protection, "Observed protection and network activity in the last minute.", new()
        {
            ["acquired"] = Delta(s.Acquired, old.Acquired), ["released"] = Delta(s.Released, old.Released),
            ["release_reasons"] = ReleaseChanges(s.ReleaseTotals, old.ReleaseTotals), ["support_passes"] = Delta(s.SupportPasses, old.SupportPasses),
            ["native_commands"] = Delta(s.NativeCommands, old.NativeCommands), ["blocked_client_records"] = Delta(s.BlockedData, old.BlockedData),
            ["prevented_transfers"] = Delta(s.BlockedOwner, old.BlockedOwner), ["owner_losses"] = Delta(s.OwnerLosses, old.OwnerLosses)
        });
        if (VerboseEnabled) DiagnosticLog.Write(_logger, "WeatherGuard", WeatherGuardLogTopics.Ownership, "Observed supporting ownership and network evidence in the last minute.", new (string, object?)[]
        {
            ("interval_s", _previous == null ? 0 : Math.Max(0, now - _lastSummary)), ("blocked_broadcasts", Delta(s.BlockedBroadcast, old.BlockedBroadcast)),
            ("routed_commands", Delta(s.Commands, old.Commands)), ("lifetime_release_reasons", Counts(s.ReleaseTotals)), ("lifetime_acquired", s.Acquired),
            ("lifetime_released", s.Released), ("lifetime_native_weather_due", s.WeatherGates), ("regions", s.Regions), ("nearby_records", s.Nearby), ("distant_records", s.Distant)
        }, LogLevel.Debug, verbose: true);
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
        if (!Enabled) return;
        var fields = new Dictionary<string, object?>(watch.Identity)
        {
            ["managed"] = watch.Protected, ["health_before"] = previous.Health, ["health_after"] = DiagnosticLog.Kept(watch.Health),
            ["health_lost"] = watch.HealthLost - previous.Lost, ["weather_cause"] = "unconfirmed"
        };
        Write(WeatherGuardLogTopics.Rain, "Observed health loss since the previous sample; weather cause is unconfirmed.", fields);
        if (VerboseEnabled) DiagnosticLog.Write(_logger, "WeatherGuard", WeatherGuardLogTopics.Rain, Subject(watch.Identity, "Rain and health evidence since the previous sample"), new (string, object?)[]
        {
            ("eligibility", watch.Eligibility), ("health_minimum", watch.MinimumHealth), ("weather_contributions_due", Delta(watch.Gates, previous.Gates)),
            ("weather_contributions_removed", Delta(watch.WeatherBypasses, previous.Bypasses)), ("lifetime_owner_changes_observed", watch.OwnerChanges),
            ("lifetime_management_changes_observed", watch.ProtectionChanges)
        }, LogLevel.Debug, verbose: true);
    }
    public void Sample(float now, Func<string> describe) => Sample(now, "acquisition", describe);
    public void Sample(float now, string stream, Func<string> describe)
    { if (DebugEnabled) SampleEnabled(now, stream, describe); }
    private void SampleEnabled(float now, string stream, Func<string> describe)
        => Record(now, stream, () => (describe(), new Dictionary<string, object?>()));
    public void Record(float now, string stream, Func<(string Message, Dictionary<string, object?> Fields)> describe)
    {
        if (!DebugEnabled) return;
        string budgetStream = stream == "acquisition" || stream == "ownership" ? "protection_samples" :
            stream.Contains("discovery") || stream == "eligibility" || stream == "rain ended" ? "discovery_samples" : "evidence_samples";
        var topic = stream == "ownership" ? WeatherGuardLogTopics.Ownership : stream.StartsWith("rain", StringComparison.Ordinal) ? WeatherGuardLogTopics.Rain : WeatherGuardLogTopics.Protection;
        if (!_budget.Reserve(now, budgetStream, SampleLimit)) return;
        var payload = describe();
        var fields = payload.Fields;
        object? candidate = fields.TryGetValue("eligibility", out var eligibility) ? eligibility : fields.TryGetValue("reason", out var reason) ? reason : null;
        if ((stream.Contains("discovery") || stream == "eligibility") && candidate != null &&
            !Equals(candidate, "eligible") && !(LogWorldCreatedItems && Equals(candidate, "world-created"))) return;
        Write(topic, payload.Message, fields);
    }
    public bool ReserveCommand(float now) => DebugEnabled && _budget.Reserve(now, "native_commands", SampleLimit);
    public void Command(WearObservation observation, float? after, bool failed, Exception? error = null)
    {
        if (!DebugEnabled && !failed) return;
        // Name the receiver: a damage, repair or removal handler is not weather wear.
        string command = observation.Fields.TryGetValue("command", out var name) ? name as string ?? "" : "";
        string handler = command == "RPC_Damage" ? "damage handler" : command == "RPC_Repair" ? "repair handler" : command == "RPC_Remove" ? "removal handler" : "receiver";
        var fields = new Dictionary<string, object?>(observation.Identity)
        { ["command"] = command, ["health_before"] = DiagnosticLog.Kept(observation.Before), ["health_after"] = after.HasValue ? DiagnosticLog.Kept(after.Value) : "unavailable",
          ["protection"] = observation.Protected ? "active" : "unknown", ["weather_damage"] = "unconfirmed", ["object_available_after"] = after.HasValue };
        Write(WeatherGuardLogTopics.Wear, failed ? $"Native {handler} raised an exception." : $"Native {handler} ran on a protected piece; its health outcome was observed.", fields, failed ? LogLevel.Error : LogLevel.Debug, error);
        if (VerboseEnabled)
        {
            var evidence = new Dictionary<string, object?>(observation.Fields)
            { ["action"] = failed ? "native_command_failed" : "native_command_executed",
              ["health_change"] = !after.HasValue ? "unavailable" : after.Value < observation.Before ? "loss_observed" : after.Value > observation.Before ? "increase_observed" : "unchanged",
              ["damage_context"] = "native_command" };
            evidence.Remove("command");
            DiagnosticLog.Write(_logger, "WeatherGuard", WeatherGuardLogTopics.Wear, Subject(observation.Identity, "Native command evidence"), evidence, LogLevel.Debug, verbose: true);
        }
    }
    public bool TryWearSample(float now, string id, bool protectedPiece, bool exposed = false)
    {
        if (!DebugEnabled) return false;
        string stream = $"{protectedPiece}:{exposed}";
        if (!_wearStreams.TryGetValue(stream, out var window) || now < window.Start || now - window.Start >= 60)
            window = (now, new HashSet<string>());
        if (window.Ids.Count >= WearSampleLimit || !window.Ids.Add(id)) return false;
        _wearStreams[stream] = window;
        return _budget.Reserve(now, "evidence_samples", SampleLimit);
    }
    public void Wear(WearObservation observation, float? after, bool failed, Exception? error = null)
    {
        if (!DebugEnabled && !failed) return;
        string blockers = Blockers(observation.Fields, observation.Owner, observation.Warmup);
        string phase = blockers + ":" + observation.GateReached + ":" + observation.Suppressed;
        bool healthLoss = after.HasValue && after.Value < observation.Before;
        if (!failed && !healthLoss && !_wearPhases.Add(phase)) return;
        observation.Fields.TryGetValue("eligibility", out var reason);
        var fields = new Dictionary<string, object?>(observation.Identity)
        {
            ["blockers"] = blockers, ["protection"] = observation.Protected ? "active" : Equals(reason, "eligible") ? "waiting" : "excluded",
            ["health_before"] = DiagnosticLog.Kept(observation.Before), ["health_after"] = after.HasValue ? DiagnosticLog.Kept(after.Value) : null, ["weather_damage"] = "unconfirmed"
        };
        string message = failed ? "Native wear calculation failed." : !observation.Owner ? "Wear calculation remained with another owner or was unassigned." :
            observation.Warmup ? "Native wear calculation waited for loading warmup." : !observation.GateReached ? "Native weather contribution was not due." :
            observation.Suppressed ? "Removed a due native weather contribution; its health outcome was observed." : "Allowed a due native weather contribution; its health outcome was observed.";
        Write(WeatherGuardLogTopics.Wear, message, fields, failed ? LogLevel.Error : LogLevel.Debug, error);
        if (VerboseEnabled)
        {
            var evidence = new Dictionary<string, object?>(observation.Fields)
            {
                ["condition_source"] = "server_instance", ["protection_reason"] = observation.Protected ? "eligible" : Equals(reason, "eligible") ? "not_acquired" : reason,
                ["action"] = observation.Suppressed ? "weather_contribution_suppressed" : !observation.Owner ? "left_to_native_owner" :
                    !observation.GateReached ? "weather_contribution_not_due" : "native_weather_contribution_allowed",
                ["health_change"] = !after.HasValue ? "unavailable" : after.Value < observation.Before ? "loss_observed" : after.Value > observation.Before ? "increase_observed" : "unchanged",
                ["damage_source"] = "unobserved", ["managed"] = observation.Protected, ["warmup"] = observation.Warmup,
                ["native_weather_contribution_due"] = observation.GateReached, ["weather_contribution_suppressed"] = observation.Suppressed
            };
            DiagnosticLog.Write(_logger, "WeatherGuard", WeatherGuardLogTopics.Wear, Subject(observation.Identity, "Native wear evidence"), evidence, LogLevel.Debug, verbose: true);
        }
    }
    public void Coverage(CoverageSnapshot s)
    {
        string measured = s.Observed
            ? $"Eligible {s.Eligible}, protected {s.Protected}, waiting {s.Waiting}, skipped {s.Skipped}, unloaded eligible {s.UnloadedEligible}. " +
              $"Skip reasons: {Counts(s.SkipReasons)}. Wait reasons: {Counts(s.WaitReasons)}. "
            : "Eligible, protected, waiting and skipped counts are unknown. ";
        DiagnosticLog.Write(_logger, "WeatherGuard", $"Requested coverage report ({s.State}): {measured}" +
            $"Scope: retained nearby ready-player regions; observed {s.Observed}; scan age {DiagnosticLog.Value(s.ScanAge)} seconds; " +
            $"regions {s.Regions}, nearby records {s.NearbyRecords}, distant records unevaluated {s.DistantRecords}; capacity limited {s.Limited}. " +
            $"Rain protection {s.RainEnabled}; underwater protection {s.UnderwaterEnabled}. " +
            "The rest of the world is unobserved; weather-damage prevention is not verified.");
    }
    public void Dispose() => Reset();
    public void Reset() { FlushPreventions(); _windowStart = DiagnosticLog.WallClock(); _capacityLimited = false; _budget.Reset(); _wearStreams.Clear(); _previous = null; _reported.Clear(); _wearPhases.Clear(); _lastSummary = 0; }
}
