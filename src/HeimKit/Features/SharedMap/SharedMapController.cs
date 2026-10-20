using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HeimKit.Shared.Players;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapController : IDisposable
{
    internal static readonly int TablePrefab = "piece_cartographytable".GetStableHashCode();
    internal static SharedMapController? Active { get; private set; }
    private const int MaximumCandidates = 1024;
    private readonly SharedMap _module;
    private readonly SharedMapSettings _settings;
    private readonly IPlayerPresence _presence;
    private readonly SharedMapExploration _exploration;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly SharedMapPatches _patches = new();
    private readonly ZDOMan _manager;
    private readonly SharedMapPortals _portals;
    private readonly SharedMapMarkerRefresh _portalRefresh;
    private readonly SharedMapResources? _resources;
    private readonly SharedMapDungeons? _dungeons;
    private readonly SharedMapLandmarks? _landmarks;
    private readonly SharedMapPinState _pinState;
    private readonly SharedMapPinAdministration _pinAdministration;
    private long _playerPixels, _tablePixels;
    private readonly List<ZDO> _sector = new(), _distant = new(), _tables = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly HashSet<ZDOID> _changedTables = new();
    private float _nextSample, _nextTables;
    private int _cursor, _batchDepth, _removed, _invalidExisting, _rejectedIncoming, _rejectedPlayerWrites;
    private int _acceptedChecks;

    internal bool Running => ReferenceEquals(Active, this) && _module.IsActive &&
        ReferenceEquals(_manager, ZDOMan.instance) && _presence.HasJoinedPlayers == true &&
        ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

    public SharedMapController(SharedMap module, SharedMapSettings settings, IPlayerPresence presence,
        SharedMapExploration exploration, SharedMapMarkerRefresh portalRefresh, ManualLogSource logger,
        SharedMapResourceStore? resourceStore = null, SharedMapMarkerRefresh? resourceRefresh = null, PlayerRoutedMessages? routedMessages = null,
        SharedMapDungeonStore? dungeonStore = null, SharedMapPinState? pinState = null,
        SharedMapLandmarkStore? runestones = null, SharedMapLandmarkStore? spawners = null)
    {
        _module = module; _settings = settings; _presence = presence; _exploration = exploration;
        _manager = ZDOMan.instance;
        _pinAdministration = new SharedMapPinAdministration(_manager, exploration.Size, module.StoreDirectory, ZNet.instance.GetWorldUID(),
            logger, () => settings.MaxTablesPerPass.Value, table => Reconcile(table, "selected_pin_cleanup"));
        _portals = new SharedMapPortals(_manager, exploration);
        _portalRefresh = portalRefresh;
        _diagnostics = new SharedMapDiagnostics(logger, settings.DebugLogging, () => settings.PinDebugNameFilter.Value);
        _pinState = pinState ?? new SharedMapPinState(module.StoreDirectory, ZNet.instance.GetWorldUID(), _diagnostics.PinStateChanged);
        _diagnostics.ProgressReady(settings.DungeonLootProgress.Value, settings.SharePinCheckmarks.Value, settings.AllowPlayerMapWrites.Value);
        if (settings.ResourceMarkerSync.Value && resourceStore != null && resourceRefresh != null)
            _resources = new SharedMapResources(_manager, resourceStore, resourceRefresh, exploration, settings, _diagnostics, Fail, presence,
                routedMessages ?? throw new InvalidOperationException("SharedMap resource discovery requires shared player message observations."));
        if (settings.DungeonMarkerSync.Value && dungeonStore != null)
            _dungeons = new SharedMapDungeons(_manager, dungeonStore, exploration, settings, _diagnostics);
        if (settings.RunestoneMarkerSync.Value || settings.SpawnerMarkerSync.Value)
            _landmarks = new SharedMapLandmarks(_manager, runestones, spawners, exploration, settings, _diagnostics, Fail);
    }

    public void Install()
    {
        if (Active != null) throw new InvalidOperationException("SharedMap already has an active controller.");
        if (_settings.PortalMarkerSync.Value) _portals.Refresh();
        Active = this;
        try { _resources?.Install(); _landmarks?.Install(); _patches.Install(); }
        catch { Dispose(); throw; }
    }

    internal ZNetPeer? ReadyPeer(ZRpc rpc) => _presence.Peers.FirstOrDefault(peer => peer.IsReady() && ReferenceEquals(peer.m_rpc, rpc));
    internal float ResourceHealthBaseline(ZDO zdo) => _resources?.HealthBaseline(zdo) ?? float.NaN;
    internal bool ObservesResourceRecord(int prefab) => _resources?.Observes(prefab) == true;
    internal void AcceptedResourceRecord(ZDO zdo, MapReplicationSnapshot before) => _resources?.Accepted(zdo, before);
    internal void Fail(Exception error) => _module.Fail(error);
    internal static bool Equal(byte[]? left, byte[]? right) => ReferenceEquals(left, right) || left != null && right != null && left.SequenceEqual(right);
    private static bool PayloadError(Exception error) => error is IOException || error is InvalidDataException || error is DecoderFallbackException || error is ArgumentException;

    private static bool TryCharacter(ZNetPeer peer, out Vector3 position, bool allowInterior = false)
    {
        position = default;
        if (!peer.IsReady() || peer.m_characterID.IsNone()) return false;
        var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid) return false;
        float health = actor.GetFloat(ZDOVars.s_health, float.NaN);
        if (!SharedMapExploration.Finite(health) || health <= 0 || actor.GetBool(ZDOVars.s_dead)) return false;
        position = actor.GetPosition();
        return SharedMapExploration.Finite(position.x) && SharedMapExploration.Finite(position.y) &&
            SharedMapExploration.Finite(position.z) && (allowInterior || !Character.InInterior(position));
    }

    public void Tick(float now)
    {
        if (!Running) return;
        BeginBatch();
        try { _pinAdministration.Tick(now); }
        finally { EndBatch(); }
        if (now >= _nextSample)
        {
            _nextSample = now + _exploration.Interval;
            foreach (var peer in _presence.Peers)
                if (TryCharacter(peer, out var position, allowInterior: true))
                {
                    if (Character.InInterior(position))
                    {
                        _dungeons?.SampleInterior(position, peer.m_uid, now);
                        continue;
                    }
                    _playerPixels += _exploration.Reveal(position.x, position.z);
                    _resources?.Sample(peer.m_uid, position);
                    _dungeons?.Sample(position);
                    _landmarks?.Sample(peer.m_uid, position);
                }
        }
        _resources?.Tick(now);
        _dungeons?.Tick(now);
        _landmarks?.Tick(now);
        if (now < _nextTables) return;
        _nextTables = now + _settings.TableIntervalSeconds.Value;
        if (_settings.PortalMarkerSync.Value) _portals.Refresh();
        CollectTables(out int usablePlayers);
        BeginBatch();
        try
        {
            if (_tables.Count > 0) _cursor %= _tables.Count;
            int prepared = 0;
            while (prepared < _tables.Count && prepared < _settings.MaxTablesPerPass.Value)
            {
                var table = _tables[_cursor]; _cursor = (_cursor + 1) % _tables.Count;
                Reconcile(table); prepared++;
            }
            _diagnostics.Report(now, _exploration, usablePlayers, _tables.Count, prepared,
                _settings.PortalMarkerSync.Value, PortalPins().Count, _playerPixels, _tablePixels);
            _resources?.Report(now);
            _dungeons?.Report(now);
            _landmarks?.Report();
        }
        finally { EndBatch(); }
    }

    // Input bytes have been accepted by native replication or a server-local field write.
    internal byte[]? Normalize(byte[]? incoming, byte[]? previous, out int removedDeaths)
    {
        if (!_settings.AllowPlayerMapWrites.Value)
        {
            // Never import a rejected upload, including its terrain. Only normalize prior server state.
            if (!Equal(incoming, previous))
            {
                if (_batchDepth > 0) _rejectedPlayerWrites++;
                else _diagnostics.PlayerWritesRejected(1);
            }
            try { return MergeTable(previous, out removedDeaths) ?? previous; }
            catch (Exception error) when (PayloadError(error)) { removedDeaths = 0; return previous; }
        }
        SharedMapTableData.ValidatedTable? priorTable = null;
        // Retain any previously accepted exploration even if the new writer has less terrain.
        if (previous != null && !Equal(previous, incoming))
        {
            try
            {
                int before = _exploration.ExploredPixels;
                priorTable = SharedMapTableData.Decode(_exploration.Size, previous);
                SharedMapTableData.ImportValidated(_exploration, priorTable);
                _tablePixels += _exploration.ExploredPixels - before;
            }
            catch (Exception error) when (PayloadError(error)) { /* The new valid payload may replace old malformed data. */ }
        }
        try
        {
            // Validate/preflight the complete upload before accepting any checkmark transitions.
            var incomingTable = SharedMapTableData.Decode(_exploration.Size, incoming);
            var normalized = MergeTable(incoming, out removedDeaths, incomingTable) ?? incoming;
            if (!Equal(incoming, previous))
            {
                var pins = SharedMapTableData.ReadValidatedPins(_exploration.Size, incomingTable);
                IReadOnlyList<MapPin> prior;
                try { prior = SharedMapTableData.ReadValidatedPins(_exploration.Size, priorTable); }
                catch (Exception error) when (PayloadError(error)) { prior = Array.Empty<MapPin>(); }
                int changes = _pinState.Accept(pins, prior, _settings.SharePinCheckmarks.Value);
                if (_batchDepth > 0) _acceptedChecks += changes; else _diagnostics.CheckmarksAccepted(changes);
                // Adoption alone must also remove its duplicate managed-source copy.
                normalized = MergeTable(incoming, out removedDeaths, incomingTable) ?? normalized;
            }
            return normalized;
        }
        catch (Exception error) when (PayloadError(error))
        {
            removedDeaths = 0;
            if (_batchDepth > 0) _rejectedIncoming++;
            else _diagnostics.InvalidTables(Time.unscaledTime, 1, true);
            return previous;
        }
    }

    internal void Incoming(ZDO table, TableMapSnapshot snapshot)
    {
        if (!Running || table.GetPrefab() != TablePrefab) return;
        var received = table.GetByteArray(ZDOVars.s_data);
        byte[]? normalized;
        int removed;
        try { normalized = Normalize(received, snapshot.Previous, out removed); }
        catch
        {
            // Deserialization has already accepted the upload. Roll back only this field before isolating failure.
            SharedMapPatches.Write(table, snapshot.Previous);
            throw;
        }
        if (!Equal(received, normalized)) SharedMapPatches.Write(table, normalized);
        var final = table.GetByteArray(ZDOVars.s_data);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, snapshot.Previous, received,
            "received_update", snapshot.SenderPeer, snapshot.SenderName);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, received, final,
            "server_normalization", snapshot.SenderPeer, snapshot.SenderName);
        if (!Equal(snapshot.Previous, final)) RecordChange(table.m_uid, removed, "accepted_client_update", table.DataRevision);
    }

    internal void AcceptedServerWrite(ZDO table, TableMapSnapshot snapshot)
    {
        var final = table.GetByteArray(ZDOVars.s_data);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, snapshot.Previous, snapshot.Received,
            "server_local_input", snapshot.SenderPeer, snapshot.SenderName);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, snapshot.Received, final,
            "server_normalization", snapshot.SenderPeer, snapshot.SenderName);
        if (!Equal(snapshot.Previous, final)) RecordChange(table.m_uid, snapshot.RemovedDeaths, "server_map_field_write", table.DataRevision);
    }

    internal void Reconcile(ZDO table, string reason = "nearby_reconciliation")
    {
        if (!Running || !table.IsValid() || table.GetPrefab() != TablePrefab) return;
        byte[]? merged;
        int removed;
        try { merged = MergeTable(table.GetByteArray(ZDOVars.s_data), out removed); }
        catch (Exception error) when (PayloadError(error))
        {
            if (_batchDepth > 0) _invalidExisting++;
            else _diagnostics.InvalidTables(Time.unscaledTime, 1, false);
            return;
        }
        if (merged == null) return;
        var previous = table.GetByteArray(ZDOVars.s_data);
        SharedMapPatches.Write(table, merged);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, previous, table.GetByteArray(ZDOVars.s_data), reason);
        RecordChange(table.m_uid, removed, reason, table.DataRevision);
    }

    private IReadOnlyList<MapMarker> PortalPins()
    {
        if (!_settings.PortalMarkerSync.Value) return Array.Empty<MapMarker>();
        var markers = _portals.Read(_settings.PortalClusterDistanceMeters.Value);
        var published = _portalRefresh.Apply(markers);
        _diagnostics.PortalCatalog(_portals.RegisteredCount, _portals.EligibleCount, published,
            _settings.PortalClusterDistanceMeters.Value);
        return published;
    }

    private byte[]? MergeTable(byte[]? data, out int removed, SharedMapTableData.ValidatedTable? decoded = null)
    {
        decoded ??= SharedMapTableData.Decode(_exploration.Size, data);
        int before = _exploration.ExploredPixels;
        bool repositioned = false;
        var result = SharedMapTableData.MergeValidated(_exploration, decoded, out removed, PortalPins(), _settings.PortalMarkerSync.Value,
            (name, type, owner, x, z, obsolete) =>
            {
                if (obsolete) repositioned |= _portalRefresh.AvoidObsoletePin(name, x, z);
                _diagnostics.PortalPinConflict(name, type, owner, x, z, obsolete);
            }, _resources?.Read() ?? Array.Empty<MapMarker>(), _pinAdministration.Removals.Contains,
            _dungeons?.Read() ?? Array.Empty<MapMarker>(), PublishPin,
            _landmarks?.Runestones() ?? Array.Empty<MapMarker>(), _landmarks?.Spawners() ?? Array.Empty<MapMarker>(), _diagnostics.DungeonPinCleanup, _settings.ResourceMarkerHideDistanceMeters.Value);
        if (repositioned)
            result = SharedMapTableData.MergeValidated(_exploration, decoded, out removed, PortalPins(), _settings.PortalMarkerSync.Value,
                resources: _resources?.Read() ?? Array.Empty<MapMarker>(), removePin: _pinAdministration.Removals.Contains,
                dungeons: _dungeons?.Read() ?? Array.Empty<MapMarker>(), publication: PublishPin,
                runestones: _landmarks?.Runestones() ?? Array.Empty<MapMarker>(), spawners: _landmarks?.Spawners() ?? Array.Empty<MapMarker>(),
                dungeonCleanup: _diagnostics.DungeonPinCleanup, resourceMarkerHideDistanceMeters: _settings.ResourceMarkerHideDistanceMeters.Value);
        _tablePixels += _exploration.ExploredPixels - before;
        return result;
    }

    private MapPin? PublishPin(MapPin pin, string? identity) => identity == null && _pinState.Adopted(pin)
        ? null : _pinState.Publish(pin, identity, _settings.SharePinCheckmarks.Value);

    internal void BeginBatch()
    {
        if (_batchDepth++ == 0) { _changedTables.Clear(); _removed = _invalidExisting = _rejectedIncoming = _rejectedPlayerWrites = _acceptedChecks = 0; }
    }
    internal void EndBatch()
    {
        if (_batchDepth == 0 || --_batchDepth != 0) return;
        _diagnostics.Changed(_changedTables.Count, _removed);
        _diagnostics.InvalidTables(Time.unscaledTime, _invalidExisting, false);
        _diagnostics.InvalidTables(Time.unscaledTime, _rejectedIncoming, true);
        _diagnostics.PlayerWritesRejected(_rejectedPlayerWrites);
        _diagnostics.CheckmarksAccepted(_acceptedChecks); _acceptedChecks = 0;
        _changedTables.Clear(); _removed = _invalidExisting = _rejectedIncoming = _rejectedPlayerWrites = 0;
    }
    internal void RecordChange(ZDOID table, int removedDeaths, string reason = "server_map_field_write", uint? revision = null)
    {
        if (_batchDepth > 0) { _changedTables.Add(table); _removed += removedDeaths; }
        else _diagnostics.Changed(1, removedDeaths);
        _diagnostics.TableChanged(Time.unscaledTime, table, revision, reason);
    }

    private void CollectTables(out int usablePlayers)
    {
        _tables.Clear(); _seen.Clear(); usablePlayers = 0;
        foreach (var peer in _presence.Peers)
        {
            if (!TryCharacter(peer, out var position)) continue;
            usablePlayers++;
            _sector.Clear(); _distant.Clear();
            _manager.FindSectorObjects(ZoneSystem.GetZone(position), peer.m_simulationDistance, _sector, _distant);
            foreach (var table in _sector)
                if (_tables.Count < MaximumCandidates && table.IsValid() && table.GetPrefab() == TablePrefab && _seen.Add(table.m_uid))
                    _tables.Add(table);
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Active, this)) Active = null;
        try
        {
            try { try { _resources?.Dispose(); } finally { try { _dungeons?.Dispose(); } finally { _landmarks?.Dispose(); } } }
            finally { try { _pinAdministration.Dispose(); } finally { _patches.Dispose(); } }
        }
        finally { _tables.Clear(); _sector.Clear(); _distant.Clear(); _seen.Clear(); _changedTables.Clear(); _batchDepth = 0; }
    }
}
