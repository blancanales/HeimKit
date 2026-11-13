using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Core.World;
using HeimKit.Features.StationSupply.Models;
using UnityEngine;
namespace HeimKit.Features.StationSupply;

internal sealed class StationSupplyDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly StationSupplySettings _settings;
    private readonly Dictionary<(ZDOID Station, SupplyKind? Intake, ZDOID Chest),
        (string Reason, string? StationWait, string? ChestWait, string? Rejection, ZDOID Candidate, ZDOID Selected)> _reasons = new();
    private readonly Queue<(ZDOID Station, SupplyKind? Intake, ZDOID Chest)> _order = new();
    private (int Stations, int Chests)? _lastScope;
    private readonly ProcessingTiming _timing;
    private long _transfers;
    private float _transferWindow;
    internal void Transferred(float now, int count = 1) { if (_transfers == 0) _transferWindow = now; _transfers += count; }
    internal void FlushTransfers(float now, bool force = false)
    {
        if (_transfers == 0 || !force && now >= _transferWindow && now - _transferWindow < 60) return;
        long count = _transfers; _transfers = 0;
        DiagnosticLog.Write(_log, "StationSupply", "Moved " + count + " real resources into production intakes in this activity window.");
    }
    private readonly Dictionary<(ZDOID Station, SupplyKind Intake, ZDOID Chest),
        (string Reason, string? Stage, string? StationWait, string? ChestWait, float Last, long Suppressed)> _incidents = new();
    internal StationSupplyDiagnostics(ManualLogSource log, StationSupplySettings settings)
    {
        _log = log; _settings = settings; ConfigureDiagnostics(() => settings.DebugLogging.Enabled, log, "StationSupply");
        _timing = new(log, "StationSupply", settings.DebugLogging);
        _timing.RegisterStages("world_discovery", "chest_selection", "ownership", "inventory_read", "commit", "client_refresh", "native_context", "diagnostics", "diagnostic_fields");
    }
    internal ProcessingTiming.TimedScope Measure(float now, Func<int> scope) => _timing.MeasureValue(now, scope);
    internal ProcessingTiming.StageScope Stage(string stage) => DiagnosticsEnabled ? _timing.MeasureStage(stage) : default;
    internal bool SelectionReady(float now) => DiagnosticsEnabled && (_budget.Available(now, "blocked", 6) || _budget.Available(now, "outcomes", 4));

    // Budget-denied observations are not remembered: they can be emitted when capacity returns.
    // State is bounded and belongs to this controller, so a reconnect starts fresh evidence.
    internal bool Begin(float now, string reason, ZDOID station = default, SupplyKind? intake = null, ZDOID chest = default,
        string? stationWait = null, string? chestWait = null, string? rejection = null, ZDOID candidate = default, ZDOID selected = default, bool terminal = false)
    {
        if (!DiagnosticsEnabled) return false;
        if (reason == "resource_transferred" || reason == "native_caches_refreshed") return false;
        bool progress = reason == "draining_native_state" || reason == "client_refresh_wait";
        string stream = terminal ? "outcomes" : progress ? "progress" : "blocked";
        int limit = terminal ? 4 : progress ? 2 : 6;
        var key = (station, intake, chest);
        var identity = (reason, stationWait, chestWait, rejection, candidate, selected);
        if ((_reasons.TryGetValue(key, out var previous) && previous == identity) || !RecordDue(now, stream, limit)) return false;
        if (!_reasons.ContainsKey(key))
        {
            if (_order.Count >= 256) _reasons.Remove(_order.Dequeue());
            _order.Enqueue(key);
        }
        _reasons[key] = identity;
        return true;
    }
    internal static bool Actionable(string reason) => reason == "native_state_timeout" || reason == "client_refresh_timeout" ||
        reason == "invalid_inventory" || reason == "invalid_source_inventory" || reason == "unsupported_inventory_format" ||
        reason == "invalid_resource_stack" || reason == "inventory_size_limit" || reason == "source_equals_destination";
    internal void Incident(float now, SupplyTransaction transaction, string reason)
    {
        if (!Actionable(reason)) return;
        var key = (transaction.Station.Id, transaction.Intake.Kind, transaction.Chest.Id);
        if (!AdmitIncident(now, key, reason, transaction.Stage, transaction.StationWait, transaction.ChestWait, out long suppressed)) return;
        string message = transaction.Definition.Name + " (" + transaction.Station.Id + ", " + transaction.Intake.Kind + ") from chest " + transaction.Chest.Id +
            ": " + reason.Replace('_', ' ') + ". Stage: " + (transaction.Stage ?? "not observed") +
            "; station wait: " + (transaction.StationWait ?? "not observed") + "; chest wait: " + (transaction.ChestWait ?? "not observed") +
            "; committed: " + (transaction.Committed ? "true" : "false") + ".";
        if (transaction.Committed) message += " Pending refresh acknowledgements: " + transaction.PendingRefresh + "; first peer: " + transaction.PendingPeer + ".";
        if (suppressed != 0) message += " Suppressed repeats: " + suppressed + ".";
        DiagnosticLog.Write(_log, "StationSupply", message, LogLevel.Warning);
    }
    internal void PreflightIncident(float now, string stationName, ZDOID station, SupplyKind intake, ZDOID chest, string reason)
    {
        if (!Actionable(reason) || !AdmitIncident(now, (station, intake, chest), reason, "source_inventory", null, null, out long suppressed)) return;
        string message = stationName + " (" + station + ", " + intake + ") from chest " + chest + ": " + reason.Replace('_', ' ') +
            ". Stage: source_inventory; committed: false.";
        if (suppressed != 0) message += " Suppressed repeats: " + suppressed + ".";
        DiagnosticLog.Write(_log, "StationSupply", message, LogLevel.Warning);
    }
    private bool AdmitIncident(float now, (ZDOID Station, SupplyKind Intake, ZDOID Chest) key, string reason,
        string? stage, string? stationWait, string? chestWait, out long suppressed)
    {
        suppressed = 0;
        bool known = _incidents.TryGetValue(key, out var previous);
        bool same = known && previous.Reason == reason && previous.Stage == stage && previous.StationWait == stationWait && previous.ChestWait == chestWait;
        if (same && now >= previous.Last && now - previous.Last < 300)
        { _incidents[key] = (reason, stage, stationWait, chestWait, previous.Last, previous.Suppressed + 1); return false; }
        if (!known && _incidents.Count >= 256)
        {
            var cursor = _incidents.GetEnumerator(); cursor.MoveNext(); var oldest = cursor.Current.Key; cursor.Dispose();
            _incidents.Remove(oldest);
        }
        _incidents[key] = (reason, stage, stationWait, chestWait, now, 0);
        if (same) suppressed = previous.Suppressed;
        return true;
    }
    internal void Recovered(SupplyTransaction transaction)
    {
        if (_incidents.Remove((transaction.Station.Id, transaction.Intake.Kind, transaction.Chest.Id)))
            DiagnosticLog.Write(_log, "StationSupply", "Supplying " + transaction.Definition.Name + " (" + transaction.Station.Id + ", " + transaction.Intake.Kind +
                ") from chest " + transaction.Chest.Id + " recovered; transfer and native refresh acknowledgements completed.");
    }

    internal void Evidence(float now, string reason, string? stationName, ZDO? station, SupplyIntake? intake, ZDO? chest, Quaternion rotation,
        SupplySelection selection, InventoryReadEvidence inventory, SupplyTransaction? transaction, string? detail, float remaining, PlayerWorldScope? scope = null, bool terminal = false)
    {
        try { WriteEvidence(now, reason, stationName, station, intake, chest, rotation, selection, inventory, transaction, detail, remaining, scope, terminal); }
        catch (Exception error)
        {
            // Optional evidence must not prevent resource cleanup or disable production.
            if (RecordDue(now, "diagnostic_failure", 1)) DiagnosticLog.Write(_log, "StationSupply", StationSupplyLogTopics.Supply,
                "Could not collect optional supply evidence.", level: LogLevel.Error, error: error);
        }
    }
    private void WriteEvidence(float now, string reason, string? stationName, ZDO? station, SupplyIntake? intake, ZDO? chest, Quaternion rotation,
        SupplySelection selection, InventoryReadEvidence inventory, SupplyTransaction? transaction, string? detail, float remaining, PlayerWorldScope? scope, bool terminal)
    {
        // Called only after Begin. All optional field allocation and metadata lookup stay here.
        using var fieldsTiming = Stage("diagnostic_fields");
        bool verbose = VerboseEnabled;
        var fields = new List<LogField>(verbose ? 80 : 8)
        {
            DiagnosticLog.Field("station", station?.m_uid), DiagnosticLog.Field("intake", intake?.Kind),
            DiagnosticLog.Field("chest", chest?.m_uid), DiagnosticLog.Field("reason", reason)
        };
        bool placement = reason == "no_chest_at_intake" || reason == "selected_chest_changed";
        Vector3 anchor = station == null || intake == null ? default : rotation * intake.LocalPoint;
        anchor.y = 0;
        if (placement && reason == "selected_chest_changed" && selection.Selected != null)
        {
            fields.Add(DiagnosticLog.Field("selected_chest", selection.Selected.m_uid));
            Measurements(fields, "selected", station, anchor, selection.Selected);
            fields.Add(DiagnosticLog.FieldAlways("selection_checked", selection.Checked));
        }
        else if (placement && selection.NearestRejected != null)
        {
            fields.Add(DiagnosticLog.Field("rejected_candidate", selection.NearestRejected.m_uid));
            Measurements(fields, "rejected", station, anchor, selection.NearestRejected);
            fields.Add(DiagnosticLog.Field("rejected_reason", selection.Rejection));
        }
        else if (transaction != null && reason.Contains("refresh"))
        {
            fields.Add(DiagnosticLog.Field("stage", transaction.Stage));
            fields.Add(DiagnosticLog.FieldAlways("pending_refresh", transaction.PendingRefresh));
            fields.Add(DiagnosticLog.Field("pending_peer", transaction.PendingPeer));
            fields.Add(DiagnosticLog.FieldAlways("wait_s", now - transaction.PublishedAt));
        }
        else if (transaction != null && (reason.Contains("ownership") || reason.Contains("native_state")))
        {
            fields.Add(DiagnosticLog.Field("stage", transaction.Stage));
            fields.Add(DiagnosticLog.Field("station_wait", transaction.StationWait));
            fields.Add(DiagnosticLog.Field("chest_wait", transaction.ChestWait));
            fields.Add(DiagnosticLog.FieldAlways("wait_s", now - (transaction.Committed ? transaction.PublishedAt : transaction.Started)));
        }
        else if (inventory.Status != null)
        {
            fields.Add(DiagnosticLog.Field("inventory_status", inventory.Status));
            fields.Add(DiagnosticLog.FieldAlways("stacks", inventory.Stacks));
            fields.Add(DiagnosticLog.FieldAlways("rejected_stacks", inventory.Rejected));
            fields.Add(DiagnosticLog.Field("first_rejected_prefab", inventory.FirstRejected));
        }
        else if (station != null && intake != null && reason == "station_has_no_room")
        {
            fields.Add(DiagnosticLog.FieldAlways("fuel", station.GetFloat(ZDOVars.s_fuel, intake.DefaultFuel)));
            fields.Add(DiagnosticLog.FieldAlways("queued", station.GetInt(ZDOVars.s_queued)));
            fields.Add(DiagnosticLog.FieldAlways("content", station.GetInt(ZDOVars.s_content)));
            fields.Add(DiagnosticLog.FieldAlways("capacity", intake.Capacity));
        }
        else
        {
            fields.Add(DiagnosticLog.Field("detail", detail));
            fields.Add(DiagnosticLog.Field("station_position", station?.GetPosition()));
            fields.Add(DiagnosticLog.Field("chest_position", chest?.GetPosition()));
            fields.Add(DiagnosticLog.Field("remaining_s", remaining));
        }
        if (terminal && transaction != null) fields.Insert(4, DiagnosticLog.FieldAlways("batch_size", transaction.BatchSize));
        if (verbose)
        {
            fields.Add(DiagnosticLog.Field("detail", detail));
            fields.Add(DiagnosticLog.Field("station_prefab", stationName));
            fields.Add(DiagnosticLog.Field("station_position", station?.GetPosition()));
            fields.Add(DiagnosticLog.Field("chest_position", chest?.GetPosition()));
            fields.Add(DiagnosticLog.Field("chest_prefab", chest == null ? null : PrefabRegistry.Current.Prefab(chest.GetPrefab())?.name));
            fields.Add(DiagnosticLog.Field("intake_position", station == null || intake == null ? null : (object)(station.GetPosition() + anchor)));
            fields.Add(DiagnosticLog.FieldAlways("range_m", _settings.ChestRangeMeters.Value));
            fields.Add(DiagnosticLog.FieldAlways("height_tolerance_m", _settings.ChestHeightToleranceMeters.Value));
            fields.Add(DiagnosticLog.FieldAlways("selection_checked", selection.Checked));
            fields.Add(DiagnosticLog.FieldAlways("selection_counts_measured", selection.CountsMeasured));
            if (selection.Checked)
            {
                fields.Add(DiagnosticLog.Field("selected_chest", selection.Selected?.m_uid));
                if (selection.Selected != null) Measurements(fields, "selected", station, anchor, selection.Selected);
                fields.Add(DiagnosticLog.Field("rejected_candidate", selection.NearestRejected?.m_uid));
                fields.Add(DiagnosticLog.Field("rejected_candidate_position", selection.NearestRejected?.GetPosition()));
                fields.Add(DiagnosticLog.Field("rejected_reason", selection.Rejection));
                if (selection.NearestRejected != null) Measurements(fields, "rejected", station, anchor, selection.NearestRejected);
            }
            if (selection.CountsMeasured)
            {
                fields.Add(DiagnosticLog.FieldAlways("eligible_chests", selection.Eligible));
                fields.Add(DiagnosticLog.FieldAlways("ineligible_chests", selection.Ineligible));
                fields.Add(DiagnosticLog.FieldAlways("wrong_side_chests", selection.WrongSide));
                fields.Add(DiagnosticLog.FieldAlways("wrong_height_chests", selection.WrongHeight));
                fields.Add(DiagnosticLog.FieldAlways("out_of_range_chests", selection.OutOfRange));
            }
            fields.Add(DiagnosticLog.Field("accepted_items", intake == null ? null : string.Join(",", intake.ItemNames)));
            fields.Add(DiagnosticLog.Field("selected_categories", intake?.Kind == SupplyKind.Obliterator ? _settings.ObliteratorCategories.Value : (object?)null));
            fields.Add(DiagnosticLog.Field("inventory_status", inventory.Status));
            fields.Add(DiagnosticLog.Field("inventory_subject", transaction?.Stage == "destination_inventory" ? "destination" : inventory.Status == null ? null : "source"));
            if (inventory.Status != null) { fields.Add(DiagnosticLog.FieldAlways("stacks", inventory.Stacks)); fields.Add(DiagnosticLog.FieldAlways("rejected_stacks", inventory.Rejected)); }
            if (inventory.FirstRejected != 0)
            {
                var rejected = PrefabRegistry.Current.Prefab(inventory.FirstRejected);
                fields.Add(DiagnosticLog.Field("first_rejected_prefab", inventory.FirstRejected));
                fields.Add(DiagnosticLog.Field("first_rejected_name", rejected?.name));
                fields.Add(DiagnosticLog.FieldAlways("rejected_definition_available", PrefabRegistry.Component<ItemDrop>(rejected) != null));
            }
            fields.Add(DiagnosticLog.Field("item", transaction?.Item));
            fields.Add(DiagnosticLog.Field("source_bytes", chest?.GetByteArray(ZDOVars.s_items)?.Length));
            if (station != null && intake != null)
            {
                fields.Add(DiagnosticLog.FieldAlways("fuel", station.GetFloat(ZDOVars.s_fuel, intake.DefaultFuel)));
                fields.Add(DiagnosticLog.FieldAlways("queued", station.GetInt(ZDOVars.s_queued)));
                fields.Add(DiagnosticLog.FieldAlways("content", station.GetInt(ZDOVars.s_content)));
                fields.Add(DiagnosticLog.FieldAlways("capacity", intake.Capacity));
                fields.Add(DiagnosticLog.FieldAlways("station_in_use", station.GetInt(ZDOVars.s_inUse)));
            }
            fields.Add(DiagnosticLog.Field("remaining_s", remaining));
            if (transaction != null)
            {
                Lease(fields, "station", transaction.Station); Lease(fields, "chest", transaction.Chest);
                fields.Add(DiagnosticLog.FieldAlways("committed", transaction.Committed));
                fields.Add(DiagnosticLog.Field("stage", transaction.Stage));
                fields.Add(DiagnosticLog.Field("station_wait", transaction.StationWait)); fields.Add(DiagnosticLog.Field("chest_wait", transaction.ChestWait));
                fields.Add(DiagnosticLog.FieldAlways("pending_refresh", transaction.PendingRefresh));
                fields.Add(DiagnosticLog.Field("pending_peer", transaction.PendingPeer));
                fields.Add(DiagnosticLog.Field("pending_peers", string.Join(",", transaction.PendingPeers)));
                fields.Add(DiagnosticLog.FieldAlways("wait_s", now - (transaction.Committed ? transaction.PublishedAt : transaction.Started)));
            }
            if (scope != null)
            {
                fields.Add(DiagnosticLog.Field(scope.UnavailableReason == null ? "scope_center" : "last_scope_center", scope.Center));
                fields.Add(DiagnosticLog.FieldAlways("scope_records", scope.ObservedRecords));
                fields.Add(DiagnosticLog.FieldAlways("observed_players", scope.Positions.Count));
                fields.Add(DiagnosticLog.FieldAlways("scan_range_m", _settings.ScanRangeMeters.Value));
            }
        }
        string subject = stationName == null ? "StationSupply" : stationName + " " + (intake?.Kind.ToString() ?? "station");
        if (transaction?.Item != null) subject += " (" + transaction.Item + ")";
        string outcome = reason switch
        {
            "resource_transferred" => "transferred one real resource",
            "native_caches_refreshed" => "confirmed the native client refresh acknowledgements",
            "client_refresh_wait" => "committed the transfer and is waiting for native client refresh acknowledgements",
            "player_scope_ready" => "observed a usable player scope for feeding",
            _ => "deferred feeding: " + reason.Replace('_', ' ')
        };
        var output = fields.ToArray();
        fieldsTiming.Dispose();
        DiagnosticLog.Write(_log, "StationSupply", StationSupplyLogTopics.Supply,
            subject + " " + outcome + ".", output, LogLevel.Debug, verbose: verbose);
    }
    private static float Horizontal(Vector3 offset) => (float)Math.Sqrt(offset.x * offset.x + offset.z * offset.z);
    private static void Measurements(List<LogField> fields, string prefix, ZDO? station, Vector3 anchor, ZDO candidate)
    {
        if (station == null) return;
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_distance_m", Horizontal(candidate.GetPosition() - station.GetPosition() - anchor)));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_height_m", candidate.GetPosition().y - station.GetPosition().y));
    }
    internal void Configuration(float now, StationSupplyPrefabs prefabs)
    {
        if (!RecordDue(now, "config", 1)) return;
        var names = new List<string>(); foreach (var station in prefabs.Stations.Values) names.Add(station.Name);
        DiagnosticLog.Write(_log, "StationSupply", CoreLogTopics.Config, "Configured production intake families for this controller.",
            new[] { DiagnosticLog.FieldAlways("station_prefabs", string.Join(",", names)), DiagnosticLog.FieldAlways("chest_prefabs", prefabs.Chests.Count),
                DiagnosticLog.FieldAlways("fermenters", _settings.Fermenters.Value), DiagnosticLog.FieldAlways("obliterators", _settings.Obliterators.Value),
                DiagnosticLog.Field("selected_categories", _settings.ObliteratorCategories.Value), DiagnosticLog.FieldAlways("range_m", _settings.ChestRangeMeters.Value),
                DiagnosticLog.FieldAlways("height_tolerance_m", _settings.ChestHeightToleranceMeters.Value), DiagnosticLog.FieldAlways("scan_range_m", _settings.ScanRangeMeters.Value) }, LogLevel.Debug);
    }
    private static void Lease(List<LogField> fields, string prefix, SupplyLease lease)
    {
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_owner", lease.Record.GetOwner()));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_previous_owner", lease.PreviousOwner));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_phase", lease.Phase));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_revision", lease.Record.DataRevision));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_owner_revision", lease.Record.OwnerRevision));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_source_observed", lease.SourceStateObserved));
        fields.Add(DiagnosticLog.FieldAlways(prefix + "_source_owner_revision", lease.SourceOwnerRevision));
    }
    internal void Scope(float now, int stations, int chests, SupplyCounts? counts = null)
    {
        if (!SummaryReady(now)) return;
        bool activity = counts?.HasActivity == true;
        if (!DiagnosticLog.DebugAvailable(_log, "StationSupply", requiredSlots: activity ? 2 : 1)) return;
        using var measured = Stage("diagnostics");
        string omitted = _budget.TakeOmitted();
        if ((_lastScope == (stations, chests) && omitted.Length == 0 && !activity) || (stations + chests == 0 && _lastScope == null && omitted.Length == 0 && !activity) || !SummaryDue(now)) return;
        _lastScope = (stations, chests);
        DiagnosticLog.Write(_log, "StationSupply", CoreLogTopics.Scope, "Observed production stations and supply chests this pass.",
            new[] { DiagnosticLog.FieldAlways("stations", stations), DiagnosticLog.FieldAlways("chests", chests), DiagnosticLog.Field("omitted", omitted) }, LogLevel.Debug);
        if (activity && counts != null)
        {
            bool verbose = VerboseEnabled;
            var fields = new List<LogField>
            {
                DiagnosticLog.FieldAlways("transfers", counts.Transfers), DiagnosticLog.FieldAlways("transactions", counts.Transactions),
                DiagnosticLog.FieldAlways("cancelled", counts.Cancelled), DiagnosticLog.FieldAlways("failures", counts.Failures),
                DiagnosticLog.FieldAlways("source_drains", counts.SourceDrains), DiagnosticLog.FieldAlways("cache_resets", counts.CacheResets),
                DiagnosticLog.FieldAlways("refresh_completed", counts.RefreshCompleted), DiagnosticLog.Field("blocked_checks", counts.BlockedChecks())
            };
            if (verbose)
            {
                fields.Add(DiagnosticLog.FieldAlways("state_snapshots", counts.StateSnapshots));
                fields.Add(DiagnosticLog.FieldAlways("manual_queued", counts.ManualQueued));
                fields.Add(DiagnosticLog.FieldAlways("manual_yielded", counts.ManualYielded));
                fields.Add(DiagnosticLog.FieldAlways("manual_replayed", counts.ManualReplayed));
                fields.Add(DiagnosticLog.FieldAlways("manual_unavailable", counts.ManualUnavailable));
            }
            DiagnosticLog.Write(_log, "StationSupply", StationSupplyLogTopics.Supply, "Observed supply activity since the previous activity report.", fields.ToArray(), LogLevel.Debug, verbose: verbose);
            counts.Reset();
        }
    }
}
