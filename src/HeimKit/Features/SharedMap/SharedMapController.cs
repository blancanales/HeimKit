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
    private readonly SharedMapPortalRefresh _portalRefresh;
    private long _playerPixels, _tablePixels;
    private readonly List<ZDO> _sector = new(), _distant = new(), _tables = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly HashSet<ZDOID> _changedTables = new();
    private float _nextSample, _nextTables;
    private int _cursor, _batchDepth, _removed, _invalidExisting, _rejectedIncoming;

    internal bool Running => ReferenceEquals(Active, this) && _module.IsActive &&
        ReferenceEquals(_manager, ZDOMan.instance) && _presence.HasJoinedPlayers == true &&
        ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

    public SharedMapController(SharedMap module, SharedMapSettings settings, IPlayerPresence presence,
        SharedMapExploration exploration, SharedMapPortalRefresh portalRefresh, ManualLogSource logger)
    {
        _module = module; _settings = settings; _presence = presence; _exploration = exploration;
        _manager = ZDOMan.instance;
        _portals = new SharedMapPortals(_manager, exploration);
        _portalRefresh = portalRefresh;
        _diagnostics = new SharedMapDiagnostics(logger, settings.DebugLogging);
    }

    public void Install()
    {
        if (Active != null) throw new InvalidOperationException("SharedMap already has an active controller.");
        if (_settings.PortalMarkerSync.Value) _portals.Refresh();
        Active = this;
        try { _patches.Install(); }
        catch { Dispose(); throw; }
    }

    internal bool IsReadyPeer(ZRpc rpc) => _presence.Peers.Any(peer => peer.IsReady() && ReferenceEquals(peer.m_rpc, rpc));
    internal void Fail(Exception error) => _module.Fail(error);
    internal static bool Equal(byte[]? left, byte[]? right) => ReferenceEquals(left, right) || left != null && right != null && left.SequenceEqual(right);
    private static bool PayloadError(Exception error) => error is IOException || error is DecoderFallbackException || error is ArgumentException;

    private static bool TryCharacter(ZNetPeer peer, out Vector3 position)
    {
        position = default;
        if (!peer.IsReady() || peer.m_characterID.IsNone()) return false;
        var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid) return false;
        float health = actor.GetFloat(ZDOVars.s_health, float.NaN);
        if (!SharedMapExploration.Finite(health) || health <= 0 || actor.GetBool(ZDOVars.s_dead)) return false;
        position = actor.GetPosition();
        return SharedMapExploration.Finite(position.x) && SharedMapExploration.Finite(position.y) &&
            SharedMapExploration.Finite(position.z) && !Character.InInterior(position);
    }

    public void Tick(float now)
    {
        if (!Running) return;
        if (now >= _nextSample)
        {
            _nextSample = now + _exploration.Interval;
            foreach (var peer in _presence.Peers)
                if (TryCharacter(peer, out var position)) _playerPixels += _exploration.Reveal(position.x, position.z);
        }
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
        }
        finally { EndBatch(); }
    }

    // Input bytes have been accepted by native replication or a server-local field write.
    internal byte[]? Normalize(byte[]? incoming, byte[]? previous, out int removedDeaths)
    {
        // Retain any previously accepted exploration even if the new writer has less terrain.
        if (previous != null && !Equal(previous, incoming))
        {
            try
            {
                int before = _exploration.ExploredPixels;
                SharedMapTableData.Import(_exploration, previous);
                _tablePixels += _exploration.ExploredPixels - before;
            }
            catch (Exception error) when (PayloadError(error)) { /* The new valid payload may replace old malformed data. */ }
        }
        try { return MergeTable(incoming, out removedDeaths) ?? incoming; }
        catch (Exception error) when (PayloadError(error))
        {
            removedDeaths = 0;
            if (_batchDepth > 0) _rejectedIncoming++;
            else _diagnostics.InvalidTables(Time.unscaledTime, 1, true);
            return previous;
        }
    }

    internal void Incoming(ZDO table, byte[]? previous)
    {
        if (!Running || table.GetPrefab() != TablePrefab) return;
        var actual = table.GetByteArray(ZDOVars.s_data);
        var normalized = Normalize(actual, previous, out int removed);
        if (Equal(actual, normalized)) return;
        SharedMapPatches.Write(table, normalized);
        RecordChange(table.m_uid, removed, "accepted_client_update", table.DataRevision);
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
        SharedMapPatches.Write(table, merged);
        RecordChange(table.m_uid, removed, reason, table.DataRevision);
    }

    private IReadOnlyList<PortalMarker> PortalPins()
    {
        if (!_settings.PortalMarkerSync.Value) return Array.Empty<PortalMarker>();
        var markers = _portals.Read(_settings.PortalClusterDistanceMeters.Value);
        var published = _portalRefresh.Apply(markers);
        _diagnostics.PortalCatalog(_portals.RegisteredCount, _portals.EligibleCount, published,
            _settings.PortalClusterDistanceMeters.Value);
        return published;
    }

    private byte[]? MergeTable(byte[]? data, out int removed)
    {
        int before = _exploration.ExploredPixels;
        bool repositioned = false;
        var result = SharedMapTableData.Merge(_exploration, data, out removed, PortalPins(), _settings.PortalMarkerSync.Value,
            (name, type, owner, x, z, obsolete) =>
            {
                if (obsolete) repositioned |= _portalRefresh.AvoidObsoletePin(name, x, z);
                _diagnostics.PortalPinConflict(name, type, owner, x, z, obsolete);
            });
        if (repositioned)
            result = SharedMapTableData.Merge(_exploration, data, out removed, PortalPins(), _settings.PortalMarkerSync.Value);
        _tablePixels += _exploration.ExploredPixels - before;
        return result;
    }

    internal void BeginBatch()
    {
        if (_batchDepth++ == 0) { _changedTables.Clear(); _removed = _invalidExisting = _rejectedIncoming = 0; }
    }
    internal void EndBatch()
    {
        if (_batchDepth == 0 || --_batchDepth != 0) return;
        _diagnostics.Changed(_changedTables.Count, _removed);
        _diagnostics.InvalidTables(Time.unscaledTime, _invalidExisting, false);
        _diagnostics.InvalidTables(Time.unscaledTime, _rejectedIncoming, true);
        _changedTables.Clear(); _removed = _invalidExisting = _rejectedIncoming = 0;
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
        try { _patches.Dispose(); }
        finally { _tables.Clear(); _sector.Clear(); _distant.Clear(); _seen.Clear(); _changedTables.Clear(); _batchDepth = 0; }
    }
}
