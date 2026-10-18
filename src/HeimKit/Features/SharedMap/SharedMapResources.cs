using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;
using HeimKit.Shared.Players;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapResources : IDisposable
{
    private const int CandidatesPerPlayer = 256;
    private readonly ZDOMan _world;
    private readonly SharedMapSilverDiscovery _silverDiscovery;
    private readonly List<ZDO> _miningNear = new();
    private readonly SharedMapResourceStore _store;
    private readonly SharedMapMarkerRefresh _refresh;
    private readonly SharedMapExploration _map;
    private readonly SharedMapSettings _settings;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly Action<Exception> _failed;
    private readonly Dictionary<int, SharedMapResourcePrefab> _prefabs = new();
    private readonly List<SharedMapResourcePrefab> _scanPrefabs = new();
    private readonly List<ZDO> _near = new(), _scan = new();
    private readonly List<ZDOID> _candidates = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly Dictionary<long, int> _playerCursors = new();
    private readonly Queue<ZDOID> _retrospective = new();
    private long _markerRevision = -1;
    private float _distance = -1, _nextRetro;
    private bool _removeDepleted, _disposed;
    private int _prefabCursor, _sectorCursor, _added, _retired;
    private IReadOnlyList<MapMarker> _markers = Array.Empty<MapMarker>();

    public SharedMapResources(ZDOMan world, SharedMapResourceStore store, SharedMapMarkerRefresh refresh,
        SharedMapExploration map, SharedMapSettings settings, SharedMapDiagnostics diagnostics, Action<Exception> failed,
        IPlayerPresence presence, PlayerRoutedMessages messages)
    {
        _world = world; _store = store; _refresh = refresh; _map = map; _settings = settings;
        _diagnostics = diagnostics; _failed = failed;
        _silverDiscovery = new SharedMapSilverDiscovery(world, presence, messages, MiningImpact, failed);
    }

    public void Install()
    {
        var unsupported = new List<string>();
        foreach (var entry in Catalog)
        {
            var prefab = SharedMapResourcePrefab.Read(entry.Name, entry.Kind, entry.Drop, entry.Successor);
            if (prefab == null) { unsupported.Add(entry.Name); continue; }
            _prefabs.Add(prefab.Hash, prefab); _scanPrefabs.Add(prefab);
        }
        _diagnostics.UnsupportedResources(unsupported);
        _silverDiscovery.Install();
        _world.m_onZDODestroyed += Destroyed;
    }

    public static void CheckCompatibility()
    {
        if (typeof(ZDOMan).GetField("m_onZDODestroyed")?.FieldType != typeof(Action<ZDO>) ||
            typeof(ZDOMan).GetMethod("FindSectorObjects", new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) }) == null)
            throw new MissingMemberException("Native resource record discovery/destruction APIs are unavailable.");
    }

    private static readonly (string Name, ResourceKind Kind, string Drop, string? Successor)[] Catalog = {
        ("rock4_copper", ResourceKind.Copper, "CopperOre", "rock4_copper_frac"),
        ("rock4_copper_frac", ResourceKind.Copper, "CopperOre", null),
        ("MineRock_Copper", ResourceKind.Copper, "CopperOre", null),
        ("MineRock_Tin", ResourceKind.Tin, "TinOre", null),
        ("silvervein", ResourceKind.Silver, "SilverOre", "silvervein_frac"),
        ("silvervein_frac", ResourceKind.Silver, "SilverOre", null),
        ("rock3_silver", ResourceKind.Silver, "SilverOre", "rock3_silver_frac"),
        ("rock3_silver_frac", ResourceKind.Silver, "SilverOre", null),
        ("MineRock_Obsidian", ResourceKind.Obsidian, "Obsidian", null),
        ("MineRock_Meteorite", ResourceKind.Flametal, "FlametalOreNew", null)
    };

    private bool Eligible(ZDO zdo, out SharedMapResourcePrefab prefab, out Vector3 position)
    {
        position = default; prefab = null!;
        if (!zdo.IsValid() || !zdo.Persistent || !_prefabs.TryGetValue(zdo.GetPrefab(), out prefab) ||
            zdo.GetLong(ZDOVars.s_creator) != 0) return false;
        position = zdo.GetPosition();
        return SharedMapExploration.Finite(position.x) && SharedMapExploration.Finite(position.y) &&
            SharedMapExploration.Finite(position.z) && !Character.InInterior(position);
    }

    private void Observe(ZDO zdo, SharedMapResourcePrefab prefab, Vector3 position, bool discoverSilver = false, MiningProgress? handoff = null)
    {
        int before = _store.Deposits.Count;
        var old = _store.Find(prefab.Kind, position.x, position.y, position.z, out _);
        bool wasDepleted = old?.Depleted == true;
        bool wasHidden = old != null && prefab.Kind == ResourceKind.Silver && !old.SilverDiscovered;
        MiningProgress? progress = prefab.Kind == ResourceKind.Copper || prefab.Kind == ResourceKind.Silver ? handoff ?? prefab.Progress(zdo) : null;
        bool? depleted = progress.HasValue ? progress.Value.RemainingSections == 0 : prefab.Depleted(zdo);
        var accepted = _store.Observe(prefab.Kind, position.x, position.y, position.z, depleted, true, progress, discoverSilver);
        _added += _store.Deposits.Count - before;
        if (accepted != null && wasHidden && accepted.SilverDiscovered) _added++;
        if (accepted != null && !wasDepleted && accepted.Depleted) _retired++;
    }

    internal bool Observes(int prefab) => prefab == SharedMapSilverDiscovery.TerrainPrefab ||
        _prefabs.TryGetValue(prefab, out var resource) && resource.Kind == ResourceKind.Silver;

    internal float HealthBaseline(ZDO zdo) => _prefabs.TryGetValue(zdo.GetPrefab(), out var prefab) ? prefab.HealthBaseline(zdo) : float.NaN;

    internal void Accepted(ZDO zdo, MapReplicationSnapshot before)
    {
        if (zdo.GetPrefab() == SharedMapSilverDiscovery.TerrainPrefab) _silverDiscovery.AcceptedTerrain(zdo, before.Terrain);
        else if (Eligible(zdo, out var prefab, out _) && prefab.Kind == ResourceKind.Silver)
            _silverDiscovery.AcceptedSilver(zdo, before);
    }

    private void MiningImpact(Vector3 point, ZDOID directTarget)
    {
        if (!directTarget.IsNone())
        {
            var direct = _world.GetZDO(directTarget);
            // A direct Silver hit identifies that vein even when its centre lies outside the proximity radius.
            if (direct != null && Eligible(direct, out var prefab, out var position) && prefab.Kind == ResourceKind.Silver)
            {
                if (prefab.Depleted(direct) == false) Observe(direct, prefab, position, true);
                else if (prefab.ReplacesWithMining && prefab.HealthBaseline(direct) <= 0)
                    Observe(direct, prefab, position, true, prefab.InitialProgress);
                return;
            }
            // Damage to creatures, buildings, or loose items is not discovery evidence.
            var target = direct == null ? null : ZNetScene.instance.GetPrefab(direct.GetPrefab());
            if (target == null || direct!.GetLong(ZDOVars.s_creator) != 0 || target.GetComponent<Character>() != null ||
                target.GetComponent<Piece>() != null || target.GetComponent<ItemDrop>() != null ||
                target.GetComponent<MineRock>() == null && target.GetComponent<MineRock5>() == null && target.GetComponent<Destructible>() == null) return;
        }
        float radius = _settings.SilverDiscoveryRadiusMeters.Value;
        _miningNear.Clear();
        int sectors = (int)Math.Ceiling(radius / ZoneSystem.instance.m_zoneSize) + 1;
        _world.FindSectorObjects(ZoneSystem.GetZone(point), new SimulationDistance(sectors, 0, true), _miningNear);
        ZDO? nearest = null; double best = (double)radius * radius;
        foreach (var zdo in _miningNear)
        {
            if (!Eligible(zdo, out var prefab, out var p) || prefab.Kind != ResourceKind.Silver || prefab.Depleted(zdo) != false ||
                _store.Find(ResourceKind.Silver, p.x, p.y, p.z, out _)?.SilverDiscovered == true) continue;
            double dx = (double)p.x - point.x, dz = (double)p.z - point.z, distance = dx * dx + dz * dz;
            if (distance > best) continue;
            if (distance == best && nearest != null && String.CompareOrdinal(zdo.m_uid.ToString(), nearest.m_uid.ToString()) >= 0) continue;
            nearest = zdo; best = distance;
        }
        if (nearest != null && Eligible(nearest, out var chosen, out var location)) Observe(nearest, chosen, location, true);
    }

    public void Sample(long player, Vector3 position)
    {
        _near.Clear(); _candidates.Clear(); _seen.Clear();
        // This is only a sector-record query. It does not alter native simulation or load zones.
        if (!SharedMapExploration.Finite(ZoneSystem.instance.m_zoneSize) || ZoneSystem.instance.m_zoneSize < 16)
            throw new InvalidOperationException("Unsupported native resource-sector geometry.");
        int radius = (int)Math.Ceiling(_map.Radius / ZoneSystem.instance.m_zoneSize) + 1;
        _world.FindSectorObjects(ZoneSystem.GetZone(position), new SimulationDistance(radius, 0, true), _near);
        double range = (double)_map.Radius * _map.Radius;
        foreach (var zdo in _near)
        {
            if (!Eligible(zdo, out _, out var p)) continue;
            double dx = (double)p.x - position.x, dz = (double)p.z - position.z;
            if (dx * dx + dz * dz <= range && _seen.Add(zdo.m_uid)) _candidates.Add(zdo.m_uid);
        }
        if (_candidates.Count == 0) return;
        _playerCursors.TryGetValue(player, out int cursor); cursor %= _candidates.Count;
        int count = Math.Min(CandidatesPerPlayer, _candidates.Count);
        for (int i = 0; i < count; i++)
        {
            var zdo = _world.GetZDO(_candidates[(cursor + i) % _candidates.Count]);
            if (zdo != null && Eligible(zdo, out var prefab, out var p)) Observe(zdo, prefab, p);
        }
        _playerCursors[player] = (cursor + count) % _candidates.Count;
    }

    public void Tick(float now)
    {
        if (_disposed) return;
        if (_settings.ResourceDiscoverPreviouslyExplored.Value && _scanPrefabs.Count > 0 && now >= _nextRetro)
        {
            if (_retrospective.Count == 0)
            {
                _scan.Clear();
                bool complete = _world.GetAllZDOsWithPrefabIterative(_scanPrefabs[_prefabCursor].Name, _scan, ref _sectorCursor);
                if (_scan.Count > SharedMapResourceStore.MaximumDeposits)
                    throw new InvalidOperationException("Resource scan candidate limit exceeded; discoveries were retained.");
                foreach (var zdo in _scan) _retrospective.Enqueue(zdo.m_uid);
                if (complete)
                {
                    _sectorCursor = 0;
                    if (++_prefabCursor == _scanPrefabs.Count)
                    { _prefabCursor = 0; _nextRetro = now + _settings.TableIntervalSeconds.Value; }
                }
            }
            for (int i = 0; i < 256 && _retrospective.Count > 0; i++)
            {
                var zdo = _world.GetZDO(_retrospective.Dequeue());
                if (zdo != null && Eligible(zdo, out var prefab, out var p) && _map.IsExplored(p.x, p.z)) Observe(zdo, prefab, p);
            }
        }
    }

    public IReadOnlyList<MapMarker> Read()
    {
        if (_markerRevision != _store.Revision || _distance != _settings.ResourceClusterDistanceMeters.Value ||
            _removeDepleted != _settings.ResourceRemoveDepleted.Value)
        {
            _markers = _refresh.Apply(SharedMapResourceClusters.Group(_store.Deposits,
                _settings.ResourceClusterDistanceMeters.Value, _settings.ResourceRemoveDepleted.Value));
            _markerRevision = _store.Revision; _distance = _settings.ResourceClusterDistanceMeters.Value;
            _removeDepleted = _settings.ResourceRemoveDepleted.Value;
        }
        return _markers;
    }

    public void Report(float now)
    {
        _diagnostics.ResourceResults(_added, _retired); _added = _retired = 0;
        _diagnostics.ResourceCatalog(now, _store.Deposits.Count, _markers.Count, _settings.ResourceDiscoverPreviouslyExplored.Value);
    }

    private void Destroyed(ZDO zdo)
    {
        if (_disposed) return;
        try
        {
            if (!Eligible(zdo, out var prefab, out var p) || prefab.ReplacesWithMining) return;
            if (_store.Retire(prefab.Kind, p.x, p.y, p.z)) _retired++;
        }
        catch (Exception error) { _failed(error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _world.m_onZDODestroyed -= Destroyed;
        _silverDiscovery.Dispose(); _miningNear.Clear();
        _near.Clear(); _scan.Clear(); _candidates.Clear(); _seen.Clear(); _retrospective.Clear(); _playerCursors.Clear();
        _added = _retired = 0;
    }
}
