using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HeimKit.Core.Players;
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
    private readonly CandidateSubscription _tableCandidates;
    private readonly ProcessingTiming? _timing;
    private long _playerPixels, _tablePixels;
    private readonly List<ZDO> _sector = new(), _distant = new(), _tables = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly HashSet<ZDOID> _changedTables = new();
    private int _cursor, _batchDepth, _removed, _invalidExisting, _rejectedIncoming, _rejectedPlayerWrites;
    private int _acceptedChecks;
    private readonly Dictionary<ZDOID, (byte[]? Payload, SharedMapReconciliationState State)> _reconciled = new();
    private int _reconciledBytes;
    private readonly Queue<ZDOID> _tableQueue = new();
    private SharedMapTableData.Reconciliation? _tableWork;
    private ZDOID _tableId;
    private byte[]? _tableSource;
    private SharedMapReconciliationState? _tableInputs;
    private long _tablePinRevision;
    private const int ReconciliationBytes = 16 * 1024 * 1024;
    private IReadOnlyList<MapMarker>? _portalPins;
    private long _portalPinsRevision = -1;
    private IReadOnlyList<MapMarker>? _portalInput;
    private SharedMapReconciliationState? _inputs;
    private SharedMapReconciliationState Inputs()
    {
        var portals = PortalPins(); var resources = _resources?.Read() ?? Array.Empty<MapMarker>();
        var dungeons = _dungeons?.Read() ?? Array.Empty<MapMarker>();
        var runestones = _landmarks?.Runestones() ?? Array.Empty<MapMarker>(); var spawners = _landmarks?.Spawners() ?? Array.Empty<MapMarker>();
        var structures = _landmarks?.Structures() ?? Array.Empty<MapMarker>();
        if (_inputs != null && _inputs.Pixels == _exploration.ExploredPixels && _inputs.Pins == _pinState.Revision &&
            _inputs.Removals == _pinAdministration.Removals.Revision && _inputs.PortalPublications == _portalRefresh.Revision &&
            _inputs.HideDistance == _settings.ResourceMarkerHideDistanceMeters.Value &&
            SharedMapReconciliationState.SameMarkers(_inputs.Portals, portals) && SharedMapReconciliationState.SameMarkers(_inputs.Resources, resources) &&
            SharedMapReconciliationState.SameMarkers(_inputs.Dungeons, dungeons) && SharedMapReconciliationState.SameMarkers(_inputs.Runestones, runestones) &&
            SharedMapReconciliationState.SameMarkers(_inputs.Spawners, spawners) &&
            SharedMapReconciliationState.SameMarkers(_inputs.Structures, structures)) return _inputs;
        return _inputs = new(_exploration.ExploredPixels, _pinState.Revision, _pinAdministration.Removals.Revision,
            _portalRefresh.Revision, _settings.ResourceMarkerHideDistanceMeters.Value, portals, resources, dungeons, runestones, spawners, structures);
    }

    internal bool Running => ReferenceEquals(Active, this) && _module.IsActive &&
        ReferenceEquals(_manager, ZDOMan.instance) && _presence.HasJoinedPlayers == true &&
        ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

    public SharedMapController(SharedMap module, SharedMapSettings settings, IPlayerPresence presence,
        SharedMapExploration exploration, SharedMapMarkerRefresh portalRefresh, ManualLogSource logger,
        SharedMapResourceStore? resourceStore = null, SharedMapMarkerRefresh? resourceRefresh = null, PlayerRoutedMessages? routedMessages = null,
        SharedMapDungeonStore? dungeonStore = null, SharedMapPinState? pinState = null,
        SharedMapLandmarkStore? runestones = null, SharedMapLandmarkStore? spawners = null, SharedMapLandmarkStore? structures = null, ProcessingTiming? timing = null)
    {
        _module = module; _settings = settings; _presence = presence; _exploration = exploration; _timing = timing;
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
        if (settings.RunestoneMarkerSync.Value || settings.SpawnerMarkerSync.Value || settings.StructureMarkersEnabled)
            _landmarks = new SharedMapLandmarks(_manager, runestones, spawners, structures, exploration, settings, _diagnostics, Fail);
        _tableCandidates = WorldObjectProvider.Current.Subscribe("SharedMap tables", IsTable, ScanKind.Nearby);
    }
    private static bool IsTable(ZDO record) => record.GetPrefab() == TablePrefab;

    public void Install()
    {
        if (Active != null) throw new InvalidOperationException("SharedMap already has an active controller.");
        if (_settings.PortalMarkerSync.Value) _portals.Refresh();
        Active = this;
        try { _resources?.Install(); _landmarks?.Install(); _patches.Install(); }
        catch { Dispose(); throw; }
    }

    internal ZNetPeer? ReadyPeer(ZRpc rpc) => PlayerPeerLookup.Ready(_presence.Peers, rpc);
    internal float ResourceHealthBaseline(ZDO zdo) => _resources?.HealthBaseline(zdo) ?? float.NaN;
    internal bool ObservesResourceRecord(int prefab) => _resources?.Observes(prefab) == true;
    internal void AcceptedResourceRecord(ZDO zdo, MapReplicationSnapshot before) => _resources?.Accepted(zdo, before);
    internal void Fail(Exception error) => _module.Fail(error);
    internal static bool Equal(byte[]? left, byte[]? right) => ReferenceEquals(left, right) || left != null && right != null && left.AsSpan().SequenceEqual(right);
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
        if (!_settings.DebugLogging.Suppressed) PerformanceStages.ArrivalProgress("SharedMap.tables", now, _tableQueue.Count, _tableWork != null);
        if (!Running) return;
        BeginBatch();
        try { _pinAdministration.Tick(now); }
        finally { EndBatch(); }
        TickTableWork(now);
        if (ModuleScheduler.Current.Due(this, "sampling", now, _exploration.Interval, cooperative: true))
        {
            using var sampleTiming = _timing?.MeasureStage("sampling") ?? default;
            for (int rosterIndex1 = 0; rosterIndex1 < _presence.Peers.Count && _presence.Peers[rosterIndex1] is var peer; rosterIndex1++)
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
        if (_resources != null && ModuleScheduler.Current.Due(this, "resources", now, .03f, cooperative: true))
        { using var stage = _timing?.MeasureStage("resources") ?? default; _resources.Tick(now); }
        if (_dungeons != null && ModuleScheduler.Current.Due(this, "dungeons", now, .03f, cooperative: true))
        { using var stage = _timing?.MeasureStage("dungeons") ?? default; _dungeons.Tick(now); }
        if (_dungeons != null && ModuleScheduler.Current.Due(this, "dungeon_loot", now, .03f, cooperative: true))
        { using var stage = _timing?.MeasureStage("dungeon_loot") ?? default; _dungeons.TickLoot(now); }
        if (_landmarks != null && ModuleScheduler.Current.Due(this, "landmarks", now, .03f, cooperative: true))
        { using var stage = _timing?.MeasureStage("landmarks") ?? default; _landmarks.Tick(now); }
        if (!ModuleScheduler.Current.Due(this, "tables", now, _settings.TableIntervalSeconds.Value, cooperative: true)) return;
        using var tableTiming = _timing?.MeasureStage("tables") ?? default;
        if (_settings.PortalMarkerSync.Value) _portals.Refresh();
        CollectTables(out int usablePlayers);
        BeginBatch();
        try
        {
            if (_tables.Count > 0) _cursor %= _tables.Count;
            int prepared = 0;
            if (_tableWork == null && _tableQueue.Count == 0)
                while (prepared < _tables.Count && prepared < _settings.MaxTablesPerPass.Value)
                {
                    var table = _tables[_cursor]; _cursor = (_cursor + 1) % _tables.Count;
                    _tableQueue.Enqueue(table.m_uid); prepared++;
                }
            _diagnostics.Report(now, _exploration, usablePlayers, _tables.Count, prepared,
                _settings.PortalMarkerSync.Value, PortalPins().Count, _playerPixels, _tablePixels);
            _resources?.Report(now);
            _dungeons?.Report(now);
            _landmarks?.Report();
        }
        finally { EndBatch(); }
    }

    private void CancelTableWork()
    {
        var work = _tableWork; _tableWork = null;
        _tableId = ZDOID.None; _tableSource = null; _tableInputs = null;
        work?.Dispose();
    }
    private void TickTableWork(float now)
    {
        if (_tableWork == null && _tableQueue.Count == 0) return;
        if (!ModuleScheduler.Current.Due(this, "table_work", now, .03f, cooperative: true)) return;
        if (_tableWork == null)
        {
            var table = _manager.GetZDO(_tableQueue.Dequeue());
            if (table == null || !table.IsValid() || table.GetPrefab() != TablePrefab) return;
            var data = table.GetByteArray(ZDOVars.s_data);
            var inputs = Inputs();
            if (_reconciled.TryGetValue(table.m_uid, out var prior) && Equal(data, prior.Payload) && inputs.Same(prior.State)) return;
            if (data != null && data.Length > SharedMapTableData.MaximumCompressedBytes)
            { _diagnostics.InvalidTables(now, 1, false); return; }
            _tableId = table.m_uid; _tableSource = data;
            _tableInputs = inputs; _tablePinRevision = _pinState.Revision;
            _tableWork = new SharedMapTableData.Reconciliation(_exploration, _tableSource, inputs, _settings.PortalMarkerSync.Value,
                (name, type, owner, x, z, obsolete) =>
                {
                    if (obsolete) _portalRefresh.AvoidObsoletePin(name, x, z);
                    _diagnostics.PortalPinConflict(name, type, owner, x, z, obsolete);
                }, _pinAdministration.Removals.Contains, PublishPin, _diagnostics.DungeonPinCleanup);
            _tableSource = _tableWork.Source;
        }
        var current = _manager.GetZDO(_tableId);
        if (current == null || !current.IsValid() || current.GetPrefab() != TablePrefab ||
            !_tableWork.SourceMatches(current.GetByteArray(ZDOVars.s_data)) ||
            _pinState.Revision != _tablePinRevision || _pinAdministration.Removals.Revision != _tableInputs!.Removals ||
            _portalRefresh.Revision != _tableInputs.PortalPublications || _settings.ResourceMarkerHideDistanceMeters.Value != _tableInputs.HideDistance)
        { CancelTableWork(); return; }
        int pixelsBefore = _exploration.ExploredPixels;
        bool complete;
        try { complete = _tableWork.Advance(); }
        catch (Exception error) when (PayloadError(error))
        { CancelTableWork(); _diagnostics.InvalidTables(now, 1, false); return; }
        finally { _tablePixels += _exploration.ExploredPixels - pixelsBefore; }
        _tablePinRevision = _pinState.Revision; // Publication may legitimately issue new pin identities.
        if (!complete) return;
        if (!_tableWork.CanPublish(current.GetByteArray(ZDOVars.s_data)) ||
            _portalRefresh.Revision != _tableInputs!.PortalPublications || _pinAdministration.Removals.Revision != _tableInputs.Removals)
        { CancelTableWork(); return; }
        // Native uploads are still normalized synchronously. Never overwrite a newer
        // map field or publish a partial compressed stream from the periodic pipeline.
        var output = _tableWork.Output;
        RememberReconciliation(_tableId, output ?? _tableSource, _tableInputs!);
        if (output != null)
        {
            SharedMapPatches.Write(current, output);
            _diagnostics.PinChanges(_exploration.Size, current.m_uid, current.DataRevision, _tableSource, current.GetByteArray(ZDOVars.s_data),
                "nearby_reconciliation", validatedBefore: _tableWork.Decoded, validatedAfter: _tableWork.Normalized, alreadyValidated: true, defer: _module?.DeferPinDiagnostics(current.GetPosition()) ?? false);
            RecordChange(current.m_uid, _tableWork.RemovedDeaths, "nearby_reconciliation", current.DataRevision);
        }
        CancelTableWork();
    }
    private void RememberReconciliation(ZDOID id, byte[]? payload, SharedMapReconciliationState inputs)
    {
        int length = payload?.Length ?? 0;
        if (_reconciled.TryGetValue(id, out var previous))
        { _reconciledBytes -= previous.Payload?.Length ?? 0; _reconciled.Remove(id); }
        if (length > ReconciliationBytes) return;
        if (_reconciled.Count >= 64 || _reconciledBytes + length > ReconciliationBytes)
        { _reconciled.Clear(); _reconciledBytes = 0; }
        _reconciled[id] = (payload == null ? null : (byte[])payload.Clone(), inputs);
        _reconciledBytes += length;
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
        using var timing = PerformanceStages.Measure("SharedMap", "incoming_table", bytes: table.GetByteArray(ZDOVars.s_data)?.Length ?? 0);
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
            "received_update", snapshot.SenderPeer, snapshot.SenderName, defer: _module?.DeferPinDiagnostics(table.GetPosition()) ?? false);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, received, final,
            "server_normalization", snapshot.SenderPeer, snapshot.SenderName, defer: _module?.DeferPinDiagnostics(table.GetPosition()) ?? false);
        if (!Equal(snapshot.Previous, final)) RecordChange(table.m_uid, removed, "accepted_client_update", table.DataRevision);
    }

    internal void AcceptedServerWrite(ZDO table, TableMapSnapshot snapshot)
    {
        var final = table.GetByteArray(ZDOVars.s_data);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, snapshot.Previous, snapshot.Received,
            "server_local_input", snapshot.SenderPeer, snapshot.SenderName, defer: _module?.DeferPinDiagnostics(table.GetPosition()) ?? false);
        _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, snapshot.Received, final,
            "server_normalization", snapshot.SenderPeer, snapshot.SenderName, defer: _module?.DeferPinDiagnostics(table.GetPosition()) ?? false);
        if (!Equal(snapshot.Previous, final)) RecordChange(table.m_uid, snapshot.RemovedDeaths, "server_map_field_write", table.DataRevision);
    }

    internal void Reconcile(ZDO table, string reason = "nearby_reconciliation")
    {
        using var timing = PerformanceStages.Measure("SharedMap", "table_reconcile", bytes: table.GetByteArray(ZDOVars.s_data)?.Length ?? 0);
        if (!Running || !table.IsValid() || table.GetPrefab() != TablePrefab) return;
        if (_tableWork != null && _tableId == table.m_uid) CancelTableWork();
        byte[]? merged;
        int removed;
        var data = table.GetByteArray(ZDOVars.s_data);
        var inputs = Inputs();
        if (_reconciled.TryGetValue(table.m_uid, out var cached) && Equal(data, cached.Payload) && inputs.Same(cached.State)) return;
        SharedMapTableData.ValidatedTable? decoded = null, normalized = null;
        try
        {
            decoded = SharedMapTableData.Decode(_exploration.Size, data);
            merged = MergeTable(data, out removed, decoded, _diagnostics.WantsPinChanges ? value => normalized = value : null);
        }
        catch (Exception error) when (PayloadError(error))
        {
            if (_batchDepth > 0) _invalidExisting++;
            else _diagnostics.InvalidTables(Time.unscaledTime, 1, false);
            return;
        }
        // Only retain successful reconciliation; incoming uploads always validate.
        // Any merge side effects must force another pass before cache reuse.
        RememberReconciliation(table.m_uid, merged ?? data, inputs);
        if (merged == null) return;
        var previous = table.GetByteArray(ZDOVars.s_data);
        SharedMapPatches.Write(table, merged);
        if (normalized != null) _diagnostics.PinChanges(_exploration.Size, table.m_uid, table.DataRevision, previous, table.GetByteArray(ZDOVars.s_data), reason,
            validatedBefore: decoded, validatedAfter: normalized, alreadyValidated: true, defer: _module?.DeferPinDiagnostics(table.GetPosition()) ?? false);
        RecordChange(table.m_uid, removed, reason, table.DataRevision);
    }

    private IReadOnlyList<MapMarker> PortalPins()
    {
        if (!_settings.PortalMarkerSync.Value) return Array.Empty<MapMarker>();
        var markers = _portals.Read(_settings.PortalClusterDistanceMeters.Value);
        IReadOnlyList<MapMarker> published;
        if (ReferenceEquals(markers, _portalInput) && _portalPins != null && _portalPinsRevision == _portalRefresh.Revision) published = _portalPins;
        else
        {
            published = _portalRefresh.Apply(markers);
            _portalInput = markers; _portalPins = published; _portalPinsRevision = _portalRefresh.Revision;
        }
        _diagnostics.PortalCatalog(_portals.RegisteredCount, _portals.EligibleCount, published,
            _settings.PortalClusterDistanceMeters.Value);
        return published;
    }

    private byte[]? MergeTable(byte[]? data, out int removed, SharedMapTableData.ValidatedTable? decoded = null, Action<SharedMapTableData.ValidatedTable>? validatedOutput = null)
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
            _landmarks?.Runestones() ?? Array.Empty<MapMarker>(), _landmarks?.Spawners() ?? Array.Empty<MapMarker>(), _diagnostics.DungeonPinCleanup, _settings.ResourceMarkerHideDistanceMeters.Value, validatedOutput,
            structures: _landmarks?.Structures() ?? Array.Empty<MapMarker>());
        if (repositioned)
            result = SharedMapTableData.MergeValidated(_exploration, decoded, out removed, PortalPins(), _settings.PortalMarkerSync.Value,
                resources: _resources?.Read() ?? Array.Empty<MapMarker>(), removePin: _pinAdministration.Removals.Contains,
                dungeons: _dungeons?.Read() ?? Array.Empty<MapMarker>(), publication: PublishPin,
                runestones: _landmarks?.Runestones() ?? Array.Empty<MapMarker>(), spawners: _landmarks?.Spawners() ?? Array.Empty<MapMarker>(),
                dungeonCleanup: _diagnostics.DungeonPinCleanup, resourceMarkerHideDistanceMeters: _settings.ResourceMarkerHideDistanceMeters.Value, validatedOutput: validatedOutput,
                structures: _landmarks?.Structures() ?? Array.Empty<MapMarker>());
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
        for (int rosterIndex2 = 0; rosterIndex2 < _presence.Peers.Count && _presence.Peers[rosterIndex2] is var peer; rosterIndex2++)
        {
            if (!TryCharacter(peer, out var position)) continue;
            usablePlayers++;
            _sector.Clear(); _distant.Clear();
            _tableCandidates.Query(ZoneSystem.GetZone(position), peer.m_simulationDistance, _sector, _distant);
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
        finally { CancelTableWork(); _tableQueue.Clear(); _tableCandidates.Dispose(); _reconciled.Clear(); _portalPins = null; _portalInput = null; _tables.Clear(); _sector.Clear(); _distant.Clear(); _seen.Clear(); _changedTables.Clear(); _batchDepth = 0; }
    }
}
