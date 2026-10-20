using System;
using System.Linq;
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
    private readonly Func<string> _pinNameFilter;
    private int _lastDungeonCount = -1;
    private bool? _lastDungeonRetrospective;
    private (int Sources, int Pins)? _lastDungeonCleanup;
    public SharedMapDiagnostics(ManualLogSource logger, DebugLoggingPolicy policy, Func<string>? pinNameFilter = null)
    { _logger = logger; _policy = policy; _pinNameFilter = pinNameFilter ?? (() => ""); }

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

    public void LandmarkResults(int runestones, int spawners, int retired)
    {
        if (runestones + spawners > 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result,
            $"Discovered {runestones} runestones and {spawners} recurring spawner sources.");
        if (retired > 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result,
            $"Recorded confirmed destruction of {retired} discovered spawner sources.");
    }
    public void DungeonPinCleanup(int sources, int pins)
    {
        var scope = (sources, pins);
        if (_lastDungeonCleanup == scope) return;
        _lastDungeonCleanup = scope;
        if (sources + pins == 0 || !_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "dungeon_pin_cleanup", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Prepared dungeon publication cleanup",
            new (string, object?)[] { ("nearby_sources_suppressed", sources), ("obsolete_uploaded_pins", pins),
                ("duplicate_distance_m", SharedMapDungeonPins.DuplicateDistanceMeters), ("client_read_confirmed", false) }, LogLevel.Debug);
    }
    public void LandmarkCatalog(int runestones, int spawners, IReadOnlyList<string> unavailable,
        bool runestonesEnabled, bool spawnersEnabled, int spawnerCandidates, string spawnerRejections)
    {
        if (unavailable.Count > 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            $"Skipped {unavailable.Count} incompatible runestone prefabs; review the supported game metadata.", level: LogLevel.Warning);
        if (spawnersEnabled && spawners == 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            "Spawner markers are enabled, but no compatible physical spawner types were registered; new spawner discovery is unavailable. Enable SharedMap.DebugMode for catalog rejection details.", level: LogLevel.Warning);
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Landmark prefab audit completed",
            new (string, object?)[] { ("runestone_families", runestones), ("spawner_families", spawners),
                ("runestone_marker_sync", runestonesEnabled), ("spawner_marker_sync", spawnersEnabled),
                ("spawner_candidates", spawnerCandidates), ("spawner_rejections", spawnerRejections),
                ("interior_sources", "omitted_without_verified_layout_and_gateway"), ("pin_type", 3) }, LogLevel.Debug);
    }
    public void LandmarkEvidence(string identity, UnityEngine.Vector3 point, string reason, UnityEngine.Vector3? player = null, float? radius = null)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "landmark_" + reason, 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Landmark source evidence",
            new (string, object?)[] { ("source_id", identity), ("x_m", point.x), ("y_m", point.y), ("z_m", point.z),
                ("reason", reason), ("sample_x_m", player?.x), ("sample_z_m", player?.z), ("radius_m", radius),
                ("distance_m", player.HasValue ? (double?)Math.Sqrt(Math.Pow(point.x - player.Value.x, 2) + Math.Pow(point.z - player.Value.z, 2)) : null),
                ("client_read_confirmed", false) }, LogLevel.Debug);
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

    public void PortalCatalog(int registered, int eligible, IReadOnlyList<MapMarker> markers, float clusterDistance)
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

    public void PortalPublished(MapMarker? previous, MapMarker actual, MapMarker published, int slot)
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

    public void PinChanges(int size, ZDOID table, uint revision, byte[]? before, byte[]? after,
        string stage, long? sender = null, string senderName = "")
    {
        if (!_policy.Enabled || SharedMapController.Equal(before, after)) return;
        float now = UnityEngine.Time.unscaledTime;
        try
        {
            if (!_budget.Reserve(now, "pin_comparisons", 60)) return;
            var changes = SharedMapPinChanges.Compare(SharedMapTableData.ReadPins(size, before, true), SharedMapTableData.ReadPins(size, after, true));
            if (!changes.Any) return;
            if (_budget.Reserve(now, "pin_summary_" + stage, 6))
                DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Summary, "Table pin list changed",
                    new (string, object?)[] { ("table_id", table), ("data_revision", revision), ("stage", stage),
                        ("added_pins", changes.Added.Count), ("removed_pins", changes.Removed.Count), ("updated_pins", changes.Updated.Count),
                        ("sender_peer_id", sender), ("sender_character_name", CleanPinText(senderName)),
                        ("sender_role", sender.HasValue ? "replication_peer" : "unknown"), ("writer_confirmed", false),
                        ("omitted_debug_interval", _budget.TakeOmitted()), ("client_read_confirmed", false) }, LogLevel.Debug);
            string filter = _pinNameFilter();
            foreach (var pin in changes.Removed) PinDetail(now, table, revision, stage, "removed", pin, null, sender, senderName, filter);
            foreach (var pin in changes.Added) PinDetail(now, table, revision, stage, "added", pin, null, sender, senderName, filter);
            foreach (var pair in changes.Updated) PinDetail(now, table, revision, stage, "updated", pair.After, pair.Before, sender, senderName, filter);
        }
        catch (Exception error)
        {
            // Optional evidence must never disable processing, alter an accepted field or interrupt native traffic.
            try
            {
                if (_budget.Reserve(now, "pin_comparison_unavailable", 1))
                    DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Table pin comparison unavailable",
                        new (string, object?)[] { ("table_id", table), ("stage", stage), ("exception_type", error.GetType().FullName),
                            ("removal_inferred", false) }, LogLevel.Debug);
            }
            catch { }
        }
    }

    private static string CleanPinText(string value) => new string(value.Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private void PinDetail(float now, ZDOID table, uint revision, string stage, string action, MapPin pin, MapPin? previous,
        long? sender, string senderName, string filter)
    {
        if (filter.Length > 0 && !string.Equals(pin.Name, filter, StringComparison.Ordinal)) return;
        bool generated = SharedMapTableData.ManagedOwner(pin.Owner);
        if (!_budget.Reserve(now, "pin_" + stage + "_" + action + (generated ? "_generated" : "_player"), 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Table pin " + action,
            new (string, object?)[] { ("table_id", table), ("data_revision", revision), ("stage", stage),
                ("pin_name", CleanPinText(pin.Name)), ("pin_owner_id", pin.Owner), ("pin_type", pin.Icon),
                ("x_m", pin.X), ("y_m", pin.Y), ("z_m", pin.Z), ("checked", pin.Checked), ("author", CleanPinText(pin.Author)),
                ("previous_checked", previous?.Checked), ("previous_author", previous == null ? null : CleanPinText(previous.Author)),
                ("sender_peer_id", sender), ("sender_character_name", CleanPinText(senderName)),
                ("sender_role", sender.HasValue ? "replication_peer" : "unknown"), ("writer_confirmed", false),
                ("pin_source", generated ? "generated" : "player"), ("client_read_confirmed", false) }, LogLevel.Debug);
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

    public void UnsupportedResources(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            $"Excluded {names.Count} unavailable or incompatible resource prefabs ({string.Join(", ", names)}); other resources remain eligible. Review the supported environment.", level: LogLevel.Warning);
    }

    public void ResourceResults(int added, int retired)
    {
        if (added == 0 && retired == 0) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result,
            $"Recorded {added} resource discoveries and {retired} confirmed resource removals.");
    }

    public void ResourceCatalog(float now, int deposits, int markers, bool retrospective)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "resource_catalog", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Summary, "Resource discovery and publication scope",
            new (string, object?)[] { ("discovery_records_now", deposits), ("resource_markers_last_preparation", markers),
                ("discover_previously_explored", retrospective), ("pin_type", (int)Minimap.PinType.Icon3),
                ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void ResourcePublished(MapMarker? previous, MapMarker actual, MapMarker published, int slot)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "resource_publications", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Resource marker publication selected",
            new (string, object?)[] { ("marker_id", actual.Identity), ("old_name", previous?.Name),
                ("new_name", published.Name), ("slot", slot), ("actual_x_m", actual.X), ("actual_z_m", actual.Z),
                ("published_x_m", published.X), ("published_z_m", published.Z), ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void ResourceStateOpened(bool discoveriesLoaded, bool publicationsLoaded, int count)
    {
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Resource storage selected",
            new (string, object?)[] { ("loaded_discoveries", discoveriesLoaded),
                ("loaded_publications", publicationsLoaded), ("discovery_records_now", count) }, LogLevel.Debug);
    }

    public void ResourceStateSaved(float now, int count)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "resource_state_saved", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, "Resource state save completed",
            new (string, object?)[] { ("discovery_records_now", count) }, LogLevel.Debug);
    }

    public void UnsupportedDungeons(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Lifecycle,
            $"Excluded {names.Count} incompatible dungeon prefabs ({string.Join(", ", names)}); other supported dungeons remain eligible. Review the supported environment.", level: LogLevel.Warning);
    }

    public void DungeonResults(int added)
    {
        if (added == 0) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, $"Recorded {added} dungeon entrance discoveries.");
    }

    public void DungeonReady(int prefabs, int gateways, int discoveries, float radius, float interval, bool retrospective, bool allowWrites)
    {
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon discovery metadata prepared",
            new (string, object?)[] { ("supported_location_prefabs", prefabs), ("validated_exterior_gateways", gateways),
                ("discovery_records_now", discoveries), ("radius_m", radius), ("sample_interval_s", interval),
                ("discover_previously_explored", retrospective), ("allow_player_map_writes", allowWrites),
                ("pin_type", (int)Minimap.PinType.Icon2) }, LogLevel.Debug);
    }

    public void DungeonDiscovered(MapMarker marker, string prefab, UnityEngine.Vector3 root, UnityEngine.Vector3? player, float radius)
    {
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "dungeon_discoveries", 6)) return;
        double? distance = player.HasValue ? Math.Sqrt(Math.Pow((double)marker.X - player.Value.x, 2) +
            Math.Pow((double)marker.Z - player.Value.z, 2)) : (double?)null;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon exterior entrance discovered",
            new (string, object?)[] { ("marker_id", marker.Identity), ("name", marker.Name), ("location_prefab", prefab),
                ("discovery_source", player.HasValue ? "usable_player_sample" : "persisted_community_exploration"),
                ("x_m", marker.X), ("y_m", marker.Y), ("z_m", marker.Z),
                ("location_root_x_m", root.x), ("location_root_y_m", root.y), ("location_root_z_m", root.z),
                ("sample_player_x_m", player?.x), ("sample_player_y_m", player?.y), ("sample_player_z_m", player?.z),
                ("sample_distance_m", distance), ("radius_m", radius),
                ("pin_type", (int)Minimap.PinType.Icon2), ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void DungeonCatalog(float now, int count, int pending, bool retrospective)
    {
        if (!_policy.Enabled || count == _lastDungeonCount && retrospective == _lastDungeonRetrospective ||
            !_budget.Reserve(now, "dungeon_catalog", 1, false)) return;
        _lastDungeonCount = count; _lastDungeonRetrospective = retrospective;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Summary, "Dungeon discovery scope changed",
            new (string, object?)[] { ("discovery_records_now", count), ("pending_encounters_now", pending),
                ("discover_previously_explored", retrospective), ("pin_type", (int)Minimap.PinType.Icon2),
                ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void DungeonStateOpened(bool loaded, int count)
    {
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon storage selected",
            new (string, object?)[] { ("loaded_discoveries", loaded), ("discovery_records_now", count) }, LogLevel.Debug);
    }

    public void DungeonStateSaved(float now, int count)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "dungeon_state_saved", 1, false)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, "Dungeon discoveries save completed",
            new (string, object?)[] { ("discovery_records_now", count) }, LogLevel.Debug);
    }

    public void PlayerWritesRejected(int count)
    {
        if (count == 0 || !_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, "player_writes_rejected", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, "Rejected table map-field changes under the player-write policy",
            new (string, object?)[] { ("rejected_fields_pass", count), ("allow_player_map_writes", false),
                ("writer_confirmed", false), ("client_read_confirmed", false) }, LogLevel.Debug);
    }

    public void DungeonLoot(float now, string identity, int total, int picked, bool changed, string reason)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "dungeon_loot", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon pickup evidence",
            new (string, object?)[] { ("gateway", identity), ("total_pickups", total), ("picked_pickups", picked),
                ("remaining_percent", picked < 0 ? -1 : total == 0 ? 0 : (int)Math.Ceiling(100d * (total - picked) / total)), ("changed", changed), ("reason", reason) }, LogLevel.Debug);
    }
    public void DungeonVisited(float now, string location, long peer, int gateways)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "dungeon_visit", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon interior visit observed",
            new (string, object?)[] { ("location", location), ("peer_id", peer), ("visited_gateways", gateways),
                ("evidence", "unique_saved_location_zone") }, LogLevel.Debug);
    }
    public void DungeonPriorVisit(float now, string gateway, int total, int picked)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "dungeon_visit", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon prior visit inferred from picked items",
            new (string, object?)[] { ("gateway", gateway), ("total_pickups", total), ("picked_pickups", picked),
                ("evidence", "matched_saved_pickup_flags") }, LogLevel.Debug);
    }
    public void DungeonLayout(float now, string gateway, string location, int bytes, int rooms, bool valid, string reason)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "dungeon_layout", 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon saved layout evidence",
            new (string, object?)[] { ("gateway", gateway), ("location", location), ("layout_bytes", bytes), ("room_count", rooms),
                ("native_room_bytes", SharedMapDungeonLayout.RoomBytes), ("expected_layout_bytes", 4L + (long)rooms * SharedMapDungeonLayout.RoomBytes),
                ("layout_valid", valid), ("reason", reason) }, LogLevel.Debug);
    }
    public void DungeonLootResults(int count)
    {
        if (count > 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, $"Updated pickup progress for {count} dungeon markers.");
    }
    public void PinStateChanged(string identity, MapPin pin, string reason)
    {
        string filter = _pinNameFilter();
        if (filter.Length > 0 && !string.Equals(filter, pin.Name, StringComparison.Ordinal)) return;
        if (!_policy.Enabled || !_budget.Reserve(UnityEngine.Time.unscaledTime, reason, 6)) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Shared pin state changed",
            new (string, object?)[] { ("identity", identity), ("label", pin.Name), ("owner", pin.Owner), ("checked", pin.Checked),
                ("x", pin.X), ("y", pin.Y), ("z", pin.Z), ("reason", reason) }, LogLevel.Debug);
    }
    public void CheckmarksAccepted(int count)
    {
        if (count > 0) DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Result, $"Accepted {count} shared pin checkmark changes.");
    }
    public void ProgressReady(bool loot, bool checks, bool uploads)
    {
        if (!_policy.Enabled) return;
        DiagnosticLog.Write(_logger, "SharedMap", DiagnosticEvent.Detail, "Dungeon progress and shared checkmark policy",
            new (string, object?)[] { ("dungeon_loot_progress", loot), ("share_pin_checkmarks", checks), ("allow_player_map_writes", uploads) }, LogLevel.Debug);
    }
}
