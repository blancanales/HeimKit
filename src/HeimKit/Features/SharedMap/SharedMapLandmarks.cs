using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeimKit.Features.SharedMap.Models;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

// Occupied SharedMap processing only. Prefab inspection never creates gameplay objects.
internal sealed class SharedMapLandmarks : IDisposable
{
    private readonly ZDOMan _world;
    private WorldCandidateIndex? _index;
    private readonly SharedMapLandmarkStore? _runestones, _spawners;
    private readonly SharedMapExploration _map;
    private readonly SharedMapSettings _settings;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly Action<Exception> _fail;
    private readonly Dictionary<int, SharedMapLandmarkPrefab> _runes = new(), _sources = new();
    private readonly List<ZDO> _near = new(), _scan = new();
    private readonly Queue<(ZDOID Id, Vector3? Player)> _pending = new();
    private readonly HashSet<ZDOID> _queued = new();
    private readonly Dictionary<string, ZDOID> _bindings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _sampleCursors = new();
    private readonly List<string> _scanNames = new();
    private readonly HashSet<string> _inspectedLocations = new(StringComparer.Ordinal);
    private readonly HashSet<int> _inspectedSources = new();
    private readonly Dictionary<string, int> _spawnerRejections = new(StringComparer.Ordinal);
    private int _spawnerCandidates;
    private readonly List<string> _unavailable = new();
    private readonly string _proxyName;
    private readonly int _proxyHash;
    private int _locationCursor, _prefabCursor, _scanName, _sector, _scanCursor, _addedRunes, _addedSpawners, _retired;
    private bool _metadataReady, _chunkComplete, _disposed;
    private float _maximumOffset, _nextScan;

    public SharedMapLandmarks(ZDOMan world, SharedMapLandmarkStore? runestones, SharedMapLandmarkStore? spawners,
        SharedMapExploration map, SharedMapSettings settings, SharedMapDiagnostics diagnostics, Action<Exception> fail)
    {
        _world = world; _runestones = runestones; _spawners = spawners; _map = map; _settings = settings; _diagnostics = diagnostics; _fail = fail;
        var proxy = ZoneSystem.instance.m_locationProxyPrefab;
        if (proxy == null || proxy.GetComponent<LocationProxy>() == null || proxy.GetComponent<ZNetView>()?.m_persistent != true)
            throw new InvalidOperationException("Native persistent location-proxy metadata is unavailable.");
        _proxyName = proxy.name; _proxyHash = _proxyName.GetStableHashCode();
    }
    public static void CheckCompatibility()
    {
        if (typeof(ZDOMan).GetField("m_onZDODestroyed")?.FieldType != typeof(Action<ZDO>) ||
            typeof(CreatureSpawner).GetField("m_respawnTimeMinuts")?.FieldType != typeof(float) ||
            typeof(SpawnArea).GetField("m_spawnIntervalSec")?.FieldType != typeof(float) || (int)Minimap.PinType.Icon3 != 3)
            throw new MissingMemberException("Native landmark source APIs are unavailable.");
    }
    public void Install() { if (_spawners != null) _world.m_onZDODestroyed += Destroyed; }
    private void InspectOne()
    {
        // Load at most one soft-reference location or inspect one registered prefab per occupied tick.
        if (_runestones != null && _locationCursor < ZoneSystem.instance.m_locations.Count)
        {
            var location = ZoneSystem.instance.m_locations[_locationCursor++];
            if (!location.m_prefab.IsValid || !SharedMapLandmarkPrefab.Runestones.ContainsKey(location.m_prefab.Name) ||
                !_inspectedLocations.Add(location.m_prefab.Name)) return;
            var reference = location.m_prefab;
            try
            {
                reference.Load();
                var metadata = reference.Asset == null ? null : SharedMapLandmarkPrefab.ReadRunestone(reference.Asset);
                if (metadata == null) { _unavailable.Add(reference.Name); return; }
                _runes.Add(metadata.Name.GetStableHashCode(), metadata); _maximumOffset = Math.Max(_maximumOffset, metadata.MaximumOffset);
            }
            finally { reference.Release(); }
            return;
        }
        if (_spawners != null && _prefabCursor < ZNetScene.instance.m_prefabs.Count)
        {
            var prefab = ZNetScene.instance.m_prefabs[_prefabCursor++];
            if (prefab != null) InspectSpawner(prefab);
            return;
        }
        if (_runestones != null) _unavailable.AddRange(SharedMapLandmarkPrefab.Runestones.Keys.Where(n => !_inspectedLocations.Contains(n)));
        if (_runes.Count > 0) _scanNames.Add(_proxyName);
        _scanNames.AddRange(_sources.Values.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        _metadataReady = true;
        _diagnostics.LandmarkCatalog(_runes.Count, _sources.Count, _unavailable,
            _runestones != null, _spawners != null, _spawnerCandidates,
            string.Join(",", _spawnerRejections.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value)));
        _unavailable.Clear();
        _spawnerRejections.Clear();
    }
    private void InspectSpawner(GameObject prefab)
    {
        int hash = prefab.name.GetStableHashCode();
        if (!_inspectedSources.Add(hash)) return;
        if (!SharedMapLandmarkPrefab.PhysicalSpawnerPrefab(prefab.name)) return;
        _spawnerCandidates++;
        var metadata = SharedMapLandmarkPrefab.ReadSpawner(prefab, out string rejection);
        if (metadata == null)
        {
            _spawnerRejections.TryGetValue(rejection, out int count);
            _spawnerRejections[rejection] = count + 1;
            return;
        }
        _sources.Add(hash, metadata); _maximumOffset = Math.Max(_maximumOffset, metadata.MaximumOffset * (metadata.SyncScale ? 4 : 1));
    }
    public void Sample(long peer, Vector3 player)
    {
        if (!_metadataReady) return;
        float zoneSize = ZoneSystem.instance.m_zoneSize;
        if (!SharedMapExploration.Finite(zoneSize) || zoneSize < 16) throw new InvalidOperationException("Unsupported landmark sector geometry.");
        int radius = (int)Math.Ceiling((_map.Radius + _maximumOffset) / zoneSize) + 1;
        if (radius > 16) throw new InvalidOperationException("Landmark sector extent exceeds supported bounds.");
        _index ??= new(z => z.GetPrefab() == _proxyHash || _sources.ContainsKey(z.GetPrefab()));
        _index.Read(player, radius, _near, classic: true);
        if (_near.Count > 65536) throw new InvalidOperationException("Landmark nearby candidate limit exceeded.");
        _sampleCursors.TryGetValue(peer, out int cursor);
        for (int i = 0; i < Math.Min(256, _near.Count); i++)
        { cursor %= _near.Count; Enqueue(_near[cursor++], player); }
        _sampleCursors[peer] = cursor;
    }
    private void Enqueue(ZDO zdo, Vector3? player)
    {
        if (!zdo.IsValid() || !zdo.Persistent || !(zdo.GetPrefab() == _proxyHash && _runes.ContainsKey(zdo.GetInt(ZDOVars.s_location)) || _sources.ContainsKey(zdo.GetPrefab()))) return;
        if (!_queued.Add(zdo.m_uid)) return;
        if (_pending.Count >= 65536) throw new InvalidOperationException("Landmark encounter queue limit exceeded.");
        _pending.Enqueue((zdo.m_uid, player));
    }
    private IEnumerable<(MapMarker Marker, bool Spawner)> Points(ZDO zdo)
    {
        if (!zdo.IsValid() || !zdo.Persistent) yield break;
        Vector3 root = zdo.GetPosition(); Quaternion rotation = zdo.GetRotation();
        if (!SharedMapDungeonPrefab.Finite(root) || Character.InInterior(root) || !SharedMapExploration.Finite(rotation.x) ||
            !SharedMapExploration.Finite(rotation.y) || !SharedMapExploration.Finite(rotation.z) || !SharedMapExploration.Finite(rotation.w) ||
            Math.Abs(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w - 1) > .01f) yield break;
        bool spawner = zdo.GetPrefab() != _proxyHash;
        SharedMapLandmarkPrefab? metadata;
        if (!(spawner ? _sources : _runes).TryGetValue(spawner ? zdo.GetPrefab() : zdo.GetInt(ZDOVars.s_location), out metadata)) yield break;
        Vector3 ratio = Vector3.one;
        if (spawner && metadata.SyncScale)
        {
            Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (scale == Vector3.zero)
            { float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, metadata.Scale.x); scale = scalar == metadata.Scale.x ? metadata.Scale : new Vector3(scalar, scalar, scalar); }
            ratio = new Vector3(scale.x / metadata.Scale.x, scale.y / metadata.Scale.y, scale.z / metadata.Scale.z);
            if (!SharedMapDungeonPrefab.Finite(ratio) || ratio.x <= 0 || ratio.y <= 0 || ratio.z <= 0 || ratio.x > 4 || ratio.y > 4 || ratio.z > 4) yield break;
        }
        foreach (var point in metadata.Points)
        {
            Vector3 position = root + rotation * Vector3.Scale(point.Offset, ratio);
            if (!SharedMapDungeonPrefab.Finite(position) || Character.InInterior(position)) continue;
            // Native StaticPhysics can settle a stationary source vertically. Its horizontal anchor and
            // hierarchy remain stable; concurrent vertically stacked identities are rejected by Bind.
            string identity = SharedMapLandmarkStore.SourceIdentity(metadata.Name, root.x, root.y, root.z, point.Path, spawner);
            yield return (new MapMarker(point.Label, position.x, position.y, position.z, identity), spawner);
        }
    }
    private bool Bind(string identity, ZDO zdo)
    {
        if (_ambiguous.Contains(identity)) return false;
        if (_bindings.TryGetValue(identity, out var old) && old != zdo.m_uid && _world.GetZDO(old) != null)
        { _ambiguous.Add(identity); _diagnostics.LandmarkEvidence(identity, zdo.GetPosition(), "ambiguous_persistent_source"); return false; }
        if (_bindings.Count >= 65536 && !_bindings.ContainsKey(identity)) throw new InvalidOperationException("Landmark source binding limit exceeded.");
        _bindings[identity] = zdo.m_uid; return true;
    }
    public void Tick(float now)
    {
        if (!_metadataReady) { InspectOne(); return; }
        if (_scanNames.Count > 0 && now >= _nextScan && _pending.Count < 32768)
        {
            if (_scanCursor == _scan.Count)
            {
                if (_chunkComplete)
                {
                    _chunkComplete = false; _sector = 0; _scanName++;
                    if (_scanName >= _scanNames.Count) { _scanName = 0; _nextScan = now + _settings.TableIntervalSeconds.Value; }
                }
                if (now >= _nextScan)
                {
                    _scan.Clear(); _scanCursor = 0;
                    _chunkComplete = _world.GetAllZDOsWithPrefabIterative(_scanNames[_scanName], _scan, ref _sector);
                    if (_scan.Count > 65536) throw new InvalidOperationException("Landmark retrospective candidate limit exceeded.");
                }
            }
            for (int i = 0; i < 256 && _scanCursor < _scan.Count && _pending.Count < 32768; i++) Enqueue(_scan[_scanCursor++], null);
        }
        // The source count is bounded as well as the candidate count: at most 256 actual points per tick.
        int remaining = 256, candidates = 0;
        while (remaining >= 64 && candidates++ < 256 && _pending.Count > 0)
        {
            var candidate = _pending.Dequeue(); _queued.Remove(candidate.Id);
            var zdo = _world.GetZDO(candidate.Id); if (zdo == null) continue;
            foreach (var point in Points(zdo))
            {
                remaining--;
                var store = point.Spawner ? _spawners : _runestones; if (store == null || !Bind(point.Marker.Identity, zdo)) continue;
                if (!store.Discover(point.Marker, candidate.Player?.x, candidate.Player?.z, _map.Radius, _map, candidate.Player == null)) continue;
                if (point.Spawner) _addedSpawners++; else _addedRunes++;
                _diagnostics.LandmarkEvidence(point.Marker.Identity, new Vector3(point.Marker.X, point.Marker.Y, point.Marker.Z),
                    candidate.Player.HasValue ? "observed_source_proximity" : "community_explored_source_point", candidate.Player, _map.Radius);
            }
        }
    }
    private void Destroyed(ZDO zdo)
    {
        if (_disposed || _spawners == null || _spawners.Count == 0) return;
        try
        {
            // Reconnect starts with retained history but a fresh metadata cache. Audit the single terminal
            // prefab on demand rather than losing a historical source's destruction during cache warmup.
            if (!_metadataReady && _spawners.Count > 0 && !_inspectedSources.Contains(zdo.GetPrefab()))
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab != null) InspectSpawner(prefab);
            }
            // Called only at accepted terminal ZDO removal, before native release. Unload has no path here.
            bool changed = false;
            foreach (var point in Points(zdo))
                if (point.Spawner && _spawners.Contains(point.Marker.Identity) && Bind(point.Marker.Identity, zdo) && _spawners.Retire(point.Marker.Identity))
                { changed = true; _retired++; _diagnostics.LandmarkEvidence(point.Marker.Identity, zdo.GetPosition(), "accepted_terminal_zdo_destruction"); }
            // Persist terminal evidence before any reconciliation can publish the removal.
            if (changed) _spawners.Save();
        }
        catch (Exception error) { _fail(error); } // Never replace native destruction or another subscriber.
    }
    public IReadOnlyList<MapMarker> Runestones() => _runestones?.Read(_settings.RunestoneMarkerSync.Value) ?? Array.Empty<MapMarker>();
    public IReadOnlyList<MapMarker> Spawners() => _spawners?.Read(_settings.SpawnerMarkerSync.Value) ?? Array.Empty<MapMarker>();
    public void Report()
    { _diagnostics.LandmarkResults(_addedRunes, _addedSpawners, _retired); _addedRunes = _addedSpawners = _retired = 0; }
    public void Dispose()
    {
        _disposed = true; _world.m_onZDODestroyed -= Destroyed;
        _index?.Dispose(); _index = null;
        _pending.Clear(); _queued.Clear(); _near.Clear(); _scan.Clear(); _runes.Clear(); _sources.Clear();
        _bindings.Clear(); _ambiguous.Clear(); _sampleCursors.Clear(); _scanNames.Clear(); _inspectedLocations.Clear(); _unavailable.Clear();
        _inspectedSources.Clear();
        _addedRunes = _addedSpawners = _retired = 0;
    }
}
