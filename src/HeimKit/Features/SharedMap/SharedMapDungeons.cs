using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoftReferenceableAssets;
using UnityEngine;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapDungeons : IDisposable
{
    private readonly ZDOMan _world;
    private readonly SharedMapDungeonStore _store;
    private readonly SharedMapExploration _map;
    private readonly SharedMapSettings _settings;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly Dictionary<int, SharedMapDungeonPrefab> _prefabs = new();
    private readonly List<ZDO> _near = new(), _scan = new();
    private readonly Queue<(SharedMapDungeonPrefab Prefab, DungeonGateway Gateway, Vector3 Root, Quaternion Rotation, Vector3? Player)> _pending = new();
    private readonly string _proxyName;
    private readonly int _proxyHash;
    private float _maximumOffset, _nextScan;
    private int _sector, _scanCursor, _added;
    private bool _scanComplete;
    private readonly SharedMapDungeonLoot? _loot;

    public SharedMapDungeons(ZDOMan world, SharedMapDungeonStore store, SharedMapExploration map,
        SharedMapSettings settings, SharedMapDiagnostics diagnostics)
    {
        _world = world; _store = store; _map = map; _settings = settings; _diagnostics = diagnostics;
        if (settings.DungeonLootProgress.Value) _loot = new SharedMapDungeonLoot(world, store, diagnostics);
        var proxy = ZoneSystem.instance.m_locationProxyPrefab;
        if (proxy == null || proxy.GetComponent<LocationProxy>() == null || proxy.GetComponent<ZNetView>() == null)
            throw new InvalidOperationException("Native dungeon location-proxy metadata is unavailable.");
        _proxyName = proxy.name; _proxyHash = _proxyName.GetStableHashCode();
        var unavailable = new List<string>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in ZoneSystem.instance.m_locations)
        {
            if (!location.m_prefab.IsValid) continue;
            string name = location.m_prefab.Name;
            if (!SharedMapDungeonPrefab.Labels.ContainsKey(name) || !found.Add(name)) continue;
            var reference = location.m_prefab;
            if (!reference.IsValid) { unavailable.Add(name); continue; }
            var loaded = reference.Load();
            try
            {
                if (loaded != LoadResult.Succeeded || reference.Asset == null)
                    throw new InvalidDataException("Dungeon prefab could not be loaded for metadata inspection.");
                var metadata = new SharedMapDungeonPrefab(reference.Asset);
                int hash = name.GetStableHashCode();
                if (_prefabs.ContainsKey(hash)) throw new InvalidDataException("Ambiguous dungeon location hash.");
                _prefabs.Add(hash, metadata);
                _maximumOffset = Math.Max(_maximumOffset, metadata.MaximumOffset);
            }
            catch (InvalidDataException) { unavailable.Add(name); }
            finally { reference.Release(); }
        }
        _diagnostics.UnsupportedDungeons(unavailable);
        _diagnostics.DungeonReady(_prefabs.Count, _prefabs.Values.Sum(p => p.Gateways.Count), _store.Count,
            _map.Radius, _map.Interval, _settings.DungeonDiscoverPreviouslyExplored.Value, _settings.AllowPlayerMapWrites.Value);
    }

    public static void CheckCompatibility()
    {
        if (typeof(ZoneSystem).GetField("m_locations")?.FieldType != typeof(List<ZoneSystem.ZoneLocation>) ||
            typeof(ZoneSystem).GetField("m_locationProxyPrefab")?.FieldType != typeof(GameObject) ||
            typeof(Teleport).GetField("m_targetPoint")?.FieldType != typeof(Teleport) ||
            typeof(ZDOMan).GetMethod("FindSectorObjects", new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) }) == null ||
            (int)Minimap.PinType.Icon2 != 2)
            throw new MissingMemberException("Native dungeon entrance discovery APIs are unavailable.");
    }

    public void Sample(Vector3 player)
    {
        float zoneSize = ZoneSystem.instance.m_zoneSize;
        if (!SharedMapExploration.Finite(zoneSize) || zoneSize < 16)
            throw new InvalidOperationException("Unsupported native dungeon-sector geometry.");
        _near.Clear();
        int radius = (int)Math.Ceiling((_map.Radius + _maximumOffset) / zoneSize) + 1;
        _world.FindSectorObjects(ZoneSystem.GetZone(player), new SimulationDistance(radius, 0, true), _near);
        foreach (var zdo in _near) Enqueue(zdo, player);
    }

    public void SampleInterior(Vector3 player, long peer, float now)
    {
        // Native Location.GetLocation resolves interior positions through GetZoneLocation.
        // Use saved proxies rather than loaded Location instances; never reveal new entrances here.
        if (!SharedMapDungeonPrefab.Finite(player) || !Character.InInterior(player)) return;
        var zone = ZoneSystem.GetZone(player);
        _near.Clear();
        _world.FindSectorObjects(zone, new SimulationDistance(0, 0, true), _near);
        if (_near.Count > 65536) return;
        ZDO? match = null;
        foreach (var zdo in _near)
        {
            if (!zdo.IsValid() || !zdo.Persistent || zdo.GetPrefab() != _proxyHash ||
                !SharedMapDungeonPrefab.Finite(zdo.GetPosition()) || Character.InInterior(zdo.GetPosition()) ||
                ZoneSystem.GetZone(zdo.GetPosition()) != zone) continue;
            if (match != null) return; // Multiple locations in this zone cannot establish membership.
            match = zdo;
        }
        if (match == null || !_prefabs.TryGetValue(match.GetInt(ZDOVars.s_location), out var prefab)) return;
        Vector3 root = match.GetPosition(); Quaternion rotation = match.GetRotation();
        if (!SharedMapExploration.Finite(rotation.x) || !SharedMapExploration.Finite(rotation.y) ||
            !SharedMapExploration.Finite(rotation.z) || !SharedMapExploration.Finite(rotation.w)) return;
        int visited = 0;
        foreach (var gateway in prefab.Gateways)
        {
            string identity = SharedMapDungeonIdentity.Create(prefab.Name, root.x, root.y, root.z, gateway.Path);
            if (_store.Visit(identity)) visited++;
            if (_store.Contains(identity)) TrackLoot(identity, prefab, root, rotation, gateway);
        }
        if (visited > 0) _diagnostics.DungeonVisited(now, prefab.Name, peer, visited);
    }

    private void Enqueue(ZDO zdo, Vector3? player)
    {
        if (!zdo.IsValid() || !zdo.Persistent || zdo.GetPrefab() != _proxyHash ||
            !_prefabs.TryGetValue(zdo.GetInt(ZDOVars.s_location), out var prefab)) return;
        Vector3 root = zdo.GetPosition(); Quaternion rotation = zdo.GetRotation();
        if (!SharedMapDungeonPrefab.Finite(root) || Character.InInterior(root) ||
            !SharedMapExploration.Finite(rotation.x) || !SharedMapExploration.Finite(rotation.y) ||
            !SharedMapExploration.Finite(rotation.z) || !SharedMapExploration.Finite(rotation.w)) return;
        if (player.HasValue)
        {
            double dx = (double)root.x - player.Value.x, dz = (double)root.z - player.Value.z;
            double range = _map.Radius + prefab.MaximumOffset;
            if (dx * dx + dz * dz > range * range) return;
        }
        foreach (var gateway in prefab.Gateways)
        {
            if (_pending.Count >= SharedMapDungeonStore.MaximumDiscoveries)
                throw new InvalidOperationException("Dungeon encounter queue limit exceeded; discoveries and prior table data were retained.");
            _pending.Enqueue((prefab, gateway, root, rotation, player));
        }
    }

    public void Tick(float now)
    {
        // Queue real observations, including their original usable player position, before deferring evaluation.
        if ((_settings.DungeonDiscoverPreviouslyExplored.Value || _store.Count > 0 && _loot != null) && now >= _nextScan && _pending.Count < 32768)
        {
            if (_scanCursor == _scan.Count)
            {
                if (_scanComplete)
                {
                    _scanComplete = false; _sector = 0; _nextScan = now + _settings.TableIntervalSeconds.Value;
                }
                else
                {
                    _scan.Clear(); _scanCursor = 0;
                    _scanComplete = _world.GetAllZDOsWithPrefabIterative(_proxyName, _scan, ref _sector);
                    if (_scan.Count > SharedMapDungeonStore.MaximumDiscoveries)
                        throw new InvalidOperationException("Dungeon scan candidate limit exceeded; discoveries were retained.");
                }
            }
            for (int i = 0; i < 256 && _scanCursor < _scan.Count && _pending.Count < 32768; i++)
                Enqueue(_scan[_scanCursor++], null);
        }
        for (int i = 0; i < 256 && _pending.Count > 0; i++)
        {
            var candidate = _pending.Dequeue();
            Vector3 point = candidate.Root + candidate.Rotation * candidate.Gateway.Offset;
            if (!SharedMapDungeonPrefab.Finite(point) || Character.InInterior(point)) continue;
            string identity = SharedMapDungeonIdentity.Create(candidate.Prefab.Name,
                candidate.Root.x, candidate.Root.y, candidate.Root.z, candidate.Gateway.Path);
            if (_store.Contains(identity)) { TrackLoot(identity, candidate.Prefab, candidate.Root, candidate.Rotation, candidate.Gateway); continue; }
            var marker = new MapMarker(candidate.Prefab.Label, point.x, point.y, point.z, identity);
            bool added = candidate.Player.HasValue
                ? _store.DiscoverNearby(marker, candidate.Player.Value.x, candidate.Player.Value.z, _map.Radius)
                : _settings.DungeonDiscoverPreviouslyExplored.Value && _store.DiscoverExplored(marker, _map);
            if (!added) continue;
            TrackLoot(identity, candidate.Prefab, candidate.Root, candidate.Rotation, candidate.Gateway);
            _added++;
            _diagnostics.DungeonDiscovered(marker, candidate.Prefab.Name, candidate.Root, candidate.Player, _map.Radius);
        }
        if (DungeonDB.instance != null) _loot?.Tick(now, _settings.TableIntervalSeconds.Value);
    }

    private void TrackLoot(string identity, SharedMapDungeonPrefab prefab, Vector3 root, Quaternion rotation, DungeonGateway gateway)
    {
        _store.ShareVisits(prefab.Gateways.Select(g =>
            SharedMapDungeonIdentity.Create(prefab.Name, root.x, root.y, root.z, g.Path)).ToArray());
        if (new[] { "Crypt2", "Crypt3", "Crypt4", "Hildir_crypt", "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2", "MountainCave02", "Hildir_cave", "TheHole01", "MorkBorg" }.Contains(prefab.Name))
            _loot?.Track(identity, prefab, root, rotation, root + rotation * gateway.InteriorOffset);
    }

    public IReadOnlyList<MapMarker> Read() => _store.Read(_settings.DungeonLootProgress.Value);
    public void Report(float now)
    {
        _diagnostics.DungeonResults(_added);
        _diagnostics.DungeonLootResults(_loot?.TakeChanges() ?? 0);
        _added = 0;
        _diagnostics.DungeonCatalog(now, _store.Count, _pending.Count, _settings.DungeonDiscoverPreviouslyExplored.Value);
    }

    public void Dispose()
    { _pending.Clear(); _scan.Clear(); _near.Clear(); _prefabs.Clear(); _added = 0; }
}
