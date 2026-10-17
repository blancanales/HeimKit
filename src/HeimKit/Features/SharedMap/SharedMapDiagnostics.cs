using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    private int _portalSampleCursor;
    public SharedMapDiagnostics(ManualLogSource logger, DebugLoggingPolicy policy) { _logger = logger; _policy = policy; }

    public void StoreOpened(bool loaded, long world, string path, int pixels)
    {
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            loaded ? $"Loaded existing community map with {pixels} explored pixels; first-run table import skipped."
                : "No saved community map found; starting first-run cartography table import before player capture.");
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Community map storage selected",
            new (string, object?)[] { ("world_uid", world), ("map_path", path), ("loaded_existing", loaded),
                ("explored_pixels_loaded", pixels) }, LogLevel.Debug);
    }

    public void Bootstrapped(int tables, int imported, int invalid, int pixels)
    {
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result,
            $"Initialized community map from {imported} of {tables} existing cartography tables; saved {pixels} explored pixels.");
        if (invalid > 0)
            DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
                $"First-run import skipped {invalid} invalid table map payloads; existing tables were preserved. Review their data and supported environment.", level: LogLevel.Warning);
    }

    public void Changed(int count, int removedDeaths)
    {
        if (count == 0) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result,
            $"Updated shared map data for {count} cartography {(count == 1 ? "table" : "tables")}; removed {removedDeaths} shared death {(removedDeaths == 1 ? "pin" : "pins")}.");
    }

    public void InvalidTables(float now, int count, bool incoming)
    {
        if (count == 0 || !_budget.Reserve(now, incoming ? "rejected_incoming" : "invalid_tables", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            incoming ? $"Rejected {count} invalid incoming table map {(count == 1 ? "field" : "fields")}; prior data was retained. Review the table data and supported environment."
                : $"Skipped {count} unsupported or invalid cartography {(count == 1 ? "table" : "tables")}; existing data was preserved. Review the table data and supported game environment.", level: LogLevel.Warning);
    }

    public void Report(float now, SharedMapExploration exploration, int players, int tables, int prepared,
        bool markerSync, int portalCount, long playerPixels, long tablePixels)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "summary", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Summary, "Community exploration and table reconciliation scope",
            new (string, object?)[] {
                ("radius_m", exploration.Radius), ("pixel_size_m", exploration.PixelSize),
                ("grid_size", exploration.Size), ("sample_interval_s", exploration.Interval),
                ("explored_pixels_now", exploration.ExploredPixels), ("usable_players_pass", players),
                ("table_candidates_pass", tables), ("tables_prepared_pass", prepared),
                ("portal_marker_sync", markerSync), ("eligible_portal_markers_now", portalCount),
                ("player_pixels_added_controller", playerPixels),
                ("table_pixels_added_controller", tablePixels)
            }, LogLevel.Debug);
    }

    public void Saved(float now, int pixels)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "saved", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, "Community terrain save completed",
            new (string, object?)[] { ("explored_pixels_now", pixels) }, LogLevel.Debug);
    }

    public void PortalCatalog(int registered, int eligible, IReadOnlyList<PortalMarker> markers, float clusterDistance)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "portal_catalog", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Summary, "Portal catalog reconciliation scope",
            new (string, object?)[] { ("registered_portals", registered), ("eligible_explored_portals", eligible),
                ("excluded_portals", registered - eligible), ("grouped_markers", markers.Count),
                ("cluster_distance_m", clusterDistance), ("eligibility_source", "persisted_community_exploration"),
                ("pin_type", (int)Minimap.PinType.Icon4), ("pin_type_name", "Icon4") }, LogLevel.Debug);
        for (int i = 0; i < Math.Min(6, markers.Count); i++)
        {
            _portalSampleCursor %= markers.Count;
            var marker = markers[_portalSampleCursor++];
            DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Portal catalog rotating publication sample",
                new (string, object?)[] { ("marker_id", marker.Identity), ("name", marker.Name),
                    ("published_x_m", marker.X), ("published_z_m", marker.Z),
                    ("pin_type", (int)Minimap.PinType.Icon4), ("client_read_confirmed", false) }, LogLevel.Debug);
        }
    }

    public void PortalRemoved(string identity, string name)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "portal_removals", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Portal marker removed from publication state",
            new (string, object?)[] { ("marker_id", identity), ("old_name", name),
                ("reason", "no_longer_eligible_or_regrouped"), ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void PortalPinConflict(string name, int type, long owner, float x, float z, bool removed)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "portal_pin_conflicts", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail,
            removed ? "Removed obsolete table pin matching a portal" : "Preserved table pin shares a generated portal label",
            new (string, object?)[] { ("name", name), ("preserved_pin_type", type), ("owner_id", owner),
                ("x_m", x), ("z_m", z), ("generated_pin_type", (int)Minimap.PinType.Icon4),
                ("removed_from_table", removed), ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void PortalPublished(PortalMarker? previous, PortalMarker actual, PortalMarker published, int slot)
    {
        if (!_policy.Enabled) return;
        float now = UnityEngine.Time.unscaledTime;
        if (!_budget.Reserve(now, "portal_publications", 6)) return;
        double dx = previous == null ? 0 : (double)published.X - previous.X;
        double dz = previous == null ? 0 : (double)published.Z - previous.Z;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Portal marker publication selected",
            new (string, object?)[] {
                ("marker_id", actual.Identity), ("reason", previous == null ? "new_marker" : previous.Name != actual.Name ? "label_changed" : "position_changed"),
                ("old_name", previous?.Name), ("new_name", published.Name), ("slot", slot),
                ("actual_x_m", actual.X), ("actual_z_m", actual.Z), ("published_x_m", published.X), ("published_z_m", published.Z),
                ("previous_x_m", previous?.X), ("previous_z_m", previous?.Z),
                ("previous_distance_m", previous == null ? (double?)null : Math.Sqrt(dx * dx + dz * dz)),
                ("client_read_confirmed", false)
            }, LogLevel.Debug);
    }

    public void TableChanged(float now, ZDOID table, uint? revision, string reason)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "table_fields", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Table map field changed on server",
            new (string, object?)[] { ("table_id", table), ("data_revision", revision), ("reason", reason),
                ("client_read_confirmed", false), ("omitted_debug_interval", _budget.TakeOmitted()) }, LogLevel.Debug);
    }

    public void PortalStateOpened(bool loaded, string path, int count)
    {
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Portal publication state selected",
            new (string, object?)[] { ("loaded_existing", loaded), ("state_path", path), ("marker_states_loaded", count) }, LogLevel.Debug);
    }

    public void PortalStateSaved(float now, int count)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "portal_state_saved", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, "Portal publication state save completed",
            new (string, object?)[] { ("marker_states_now", count) }, LogLevel.Debug);
    }
}
