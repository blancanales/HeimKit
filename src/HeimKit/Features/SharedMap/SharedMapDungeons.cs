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
using SoftReferenceableAssets;
using UnityEngine;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapDungeons : IDisposable
{
    private readonly ZDOMan _world;
    private CandidateSubscription? _index;
    private readonly SharedMapDungeonStore _store;
    private readonly SharedMapExploration _map;
    private readonly SharedMapSettings _settings;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly PrefabCatalog<SharedMapDungeonPrefab> _prefabs = PrefabRegistry.Current.Catalog<SharedMapDungeonPrefab>("SharedMapDungeons._prefabs");
    private readonly List<ZDO> _near = new(), _scan = new();
    private readonly Queue<(SharedMapDungeonPrefab Prefab, DungeonGateway Gateway, Vector3 Root, Quaternion Rotation, Vector3? Player)> _pending = new();
    private readonly string _proxyName;
    private readonly int _proxyHash;
    private float _maximumOffset;
    private readonly ScheduleToken _nextScan = new();
    private int _sector, _scanCursor, _added;
    private bool _scanComplete;
    private readonly SharedMapDungeonLoot? _loot;

    public SharedMapDungeons(ZDOMan world, SharedMapDungeonStore store, SharedMapExploration map,
        SharedMapSettings settings, SharedMapDiagnostics diagnostics)
    {
        _world = world; _store = store; _map = map; _settings = settings; _diagnostics = diagnostics;
        if (settings.DungeonLootProgress.Value) _loot = new SharedMapDungeonLoot(world, store, diagnostics);
        var proxy = ZoneSystem.instance.m_locationProxyPrefab;
        if (proxy == null || PrefabRegistry.Component<LocationProxy>(proxy) == null || PrefabRegistry.Component<ZNetView>(proxy) == null)
            throw new InvalidOperationException("Native dungeon location-proxy metadata is unavailable.");
        _proxyName = proxy.name; _proxyHash = _proxyName.GetStableHashCode();
    }
    private readonly List<string> _unavailable = new();
    private readonly HashSet<string> _found = new(StringComparer.Ordinal);
    private int _locationCursor;
    private bool _metadataReady;
    private readonly PrefabAssetRequest _asset = new();
    private SoftReference<GameObject> _inspecting;
    private string? _inspectingName;
    private void InspectOne(float now)
    {
        if (_inspectingName != null)
        {
            if (!_asset.TryGet(_inspecting, now, out var prefab, _inspectingName)) return;
            string name = _inspectingName;
            try
            {
                if (prefab == null) throw new InvalidDataException("Dungeon asset " + _asset.LastOutcome + ".");
                using var timing = PerformanceStages.Measure("SharedMap", "dungeon_metadata", 1, subject: name);
                var metadata = new SharedMapDungeonPrefab(prefab);
                _prefabs.Add(name.GetStableHashCode(), metadata);
                _maximumOffset = Math.Max(_maximumOffset, metadata.MaximumOffset);
            }
            catch (InvalidDataException) { _unavailable.Add(name); }
            finally
            {
                try { _diagnostics.AssetPreparation(now, name, _asset.LastOutcome, _asset.LastWaitSeconds); }
                finally { try { _asset.Dispose(); } finally { _inspectingName = null; _inspecting = default; } }
            }
            return;
        }
        int examined = 0;
        while (_locationCursor < ZoneSystem.instance.m_locations.Count && examined++ < 16)
        {
            var location = ZoneSystem.instance.m_locations[_locationCursor++];
            if (!location.m_prefab.IsValid) continue;
            string name = location.m_prefab.Name;
            if (!SharedMapDungeonPrefab.Labels.ContainsKey(name) || !_found.Add(name)) continue;
            int hash = name.GetStableHashCode();
            if (_prefabs.TryReuse(hash, out var cached))
            {
                if (cached.Name != name) throw new InvalidDataException("Ambiguous cached dungeon prefab hash.");
                _maximumOffset = Math.Max(_maximumOffset, cached.MaximumOffset);
                _diagnostics.AssetPreparation(now, name, "cached", 0);
                return;
            }
            var reference = location.m_prefab;
            if (!reference.IsValid) { _unavailable.Add(name); return; }
            _inspecting = reference; _inspectingName = name;
            _asset.TryGet(reference, now, out _, name);
            return;
        }
        if (_locationCursor < ZoneSystem.instance.m_locations.Count) return;
        _metadataReady = true;
        _diagnostics.UnsupportedDungeons(_unavailable); _unavailable.Clear();
        int gateways = 0; foreach (var definition in _prefabs.Values) gateways += definition.Gateways.Count;
        _diagnostics.DungeonReady(_prefabs.Count, gateways, _store.Count,
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
        if (!_metadataReady) return;
        float zoneSize = ZoneSystem.instance.m_zoneSize;
        if (!SharedMapExploration.Finite(zoneSize) || zoneSize < 16)
            throw new InvalidOperationException("Unsupported native dungeon-sector geometry.");
        _near.Clear();
        int radius = (int)Math.Ceiling((_map.Radius + _maximumOffset) / zoneSize) + 1;
        _index ??= WorldObjectProvider.Current.Subscribe("SharedMap", z => z.GetPrefab() == _proxyHash);
        _index.Read(player, radius, _near, classic: true);
        foreach (var zdo in _near) Enqueue(zdo, player);
    }

    public void SampleInterior(Vector3 player, long peer, float now)
    {
        if (!_metadataReady) return;
        // Native Location.GetLocation resolves interior positions through GetZoneLocation.
        // Use saved proxies rather than loaded Location instances; never reveal new entrances here.
        if (!SharedMapDungeonPrefab.Finite(player) || !Character.InInterior(player)) return;
        var zone = ZoneSystem.GetZone(player);
        _near.Clear();
        _index ??= WorldObjectProvider.Current.Subscribe("SharedMap", z => z.GetPrefab() == _proxyHash);
        _index.Read(player, 0, _near, classic: true);
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
            string identity = GatewayIdentity(prefab, root, gateway.Path);
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
        if (!_metadataReady) { InspectOne(now); return; }
        // Queue real observations, including their original usable player position, before deferring evaluation.
        if ((_settings.DungeonDiscoverPreviouslyExplored.Value || _store.Count > 0 && _loot != null) && _nextScan.Ready(now) && _pending.Count < 32768)
        {
            if (_scanCursor == _scan.Count)
            {
                if (_scanComplete)
                {
                    _scanComplete = false; _sector = 0; _nextScan.Postpone(now, _settings.TableIntervalSeconds.Value);
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
            if (i > 0 && !ModuleScheduler.Current.HasBudget) break;
            var candidate = _pending.Dequeue();
            Vector3 point = candidate.Root + candidate.Rotation * candidate.Gateway.Offset;
            if (!SharedMapDungeonPrefab.Finite(point) || Character.InInterior(point)) continue;
            string identity = GatewayIdentity(candidate.Prefab, candidate.Root, candidate.Gateway.Path);
            if (_store.Contains(identity)) { TrackLoot(identity, candidate.Prefab, candidate.Root, candidate.Rotation, candidate.Gateway); continue; }
            if (candidate.Player.HasValue)
            {
                double dx = (double)point.x - candidate.Player.Value.x, dz = (double)point.z - candidate.Player.Value.z;
                if (dx * dx + dz * dz > (double)_map.Radius * _map.Radius) continue;
            }
            else if (!_settings.DungeonDiscoverPreviouslyExplored.Value || !_map.IsExplored(point.x, point.z)) continue;
            var marker = new MapMarker(candidate.Prefab.Label, point.x, point.y, point.z, identity);
            bool added = candidate.Player.HasValue
                ? _store.DiscoverNearby(marker, candidate.Player.Value.x, candidate.Player.Value.z, _map.Radius)
                : _settings.DungeonDiscoverPreviouslyExplored.Value && _store.DiscoverExplored(marker, _map);
            if (!added) continue;
            TrackLoot(identity, candidate.Prefab, candidate.Root, candidate.Rotation, candidate.Gateway);
            _added++;
            _diagnostics.DungeonDiscovered(marker, candidate.Prefab.Name, candidate.Root, candidate.Player, _map.Radius);
        }
    }
    internal void TickLoot(float now) { if (_metadataReady && DungeonDB.instance != null) _loot?.Tick(now, _settings.TableIntervalSeconds.Value); }

    private readonly Dictionary<(string Name, Vector3 Root), string[]> _gatewayIdentities = new();
    private string[] GatewayIdentities(SharedMapDungeonPrefab prefab, Vector3 root)
    {
        var key = (prefab.Name, root);
        if (_gatewayIdentities.TryGetValue(key, out var identities)) return identities;
        if (_gatewayIdentities.Count >= SharedMapDungeonStore.MaximumDiscoveries) throw new InvalidOperationException("Dungeon placed-source cache limit exceeded.");
        identities = new string[prefab.Gateways.Count];
        for (int index = 0; index < identities.Length; index++)
            identities[index] = SharedMapDungeonIdentity.Create(prefab.Name, root.x, root.y, root.z, prefab.Gateways[index].Path);
        _gatewayIdentities.Add(key, identities); return identities;
    }
    private string GatewayIdentity(SharedMapDungeonPrefab prefab, Vector3 root, string path)
    {
        var identities = GatewayIdentities(prefab, root);
        for (int index = 0; index < prefab.Gateways.Count; index++) if (prefab.Gateways[index].Path == path) return identities[index];
        throw new InvalidOperationException("Dungeon gateway is outside its validated template.");
    }
    private void TrackLoot(string identity, SharedMapDungeonPrefab prefab, Vector3 root, Quaternion rotation, DungeonGateway gateway)
    {
        _store.ShareVisits(GatewayIdentities(prefab, root));
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
    {
        try { _asset.Dispose(); }
        finally
        {
            try { _loot?.Dispose(); }
            finally
            {
                try { _index?.Dispose(); }
                finally { _index = null; _pending.Clear(); _gatewayIdentities.Clear(); _scan.Clear(); _near.Clear(); _prefabs.Clear(); _added = 0; }
            }
        }
    }
}
