using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;
// Consult replicated objects already visible to this player.
internal sealed class SharedMapLocalDiscovery : IDisposable
{
    private readonly PrefabCatalog<SharedMapResourcePrefab> _resources = PrefabRegistry.Current.Catalog<SharedMapResourcePrefab>("personal.resources");
    private readonly PrefabCatalog<SharedMapLandmarkPrefab> _landmarks = PrefabRegistry.Current.Catalog<SharedMapLandmarkPrefab>("personal.landmarks");
    private readonly PrefabCatalog<SharedMapDungeonPrefab> _dungeons = PrefabRegistry.Current.Catalog<SharedMapDungeonPrefab>("personal.dungeons");
    private readonly PrefabAssetRequest _asset = new();
    private int _resourceIndex, _locationIndex;
    private IEnumerator<bool>? _scan;
    private List<SharedMapPersonalMarker> _rows = new();
    internal readonly Dictionary<string, string> PortalAliases = new();
    private float _next;
    private readonly Queue<(int Location, Vector3 Root, Quaternion Rotation, Vector3 Door)> _entries = new();

    internal void Entered(Teleport door)
    {
        var proxy = door.GetComponentInParent<LocationProxy>();
        var record = proxy == null ? null : proxy.GetComponent<ZNetView>()?.GetZDO();
        if (record == null || !record.IsValid() || door.m_hoverText != "$location_enter") return;
        if (_entries.Count >= 16) _entries.Dequeue();
        _entries.Enqueue((record.GetInt(ZDOVars.s_location), record.GetPosition(), record.GetRotation(), door.transform.position));
    }

    private List<SharedMapPersonalMarker>? ResolveEntry()
    {
        if (_entries.Count == 0) return null;
        var entry = _entries.Peek();
        if (!_dungeons.TryGetValue(entry.Location, out var dungeon))
        {
            if (_locationIndex >= ZoneSystem.instance.m_locations.Count) _entries.Dequeue();
            return null;
        }
        _entries.Dequeue();
        foreach (var gate in dungeon.Gateways)
        {
            var point = entry.Root + entry.Rotation * gate.Offset;
            if ((point - entry.Door).sqrMagnitude > 1f) continue;
            var id = SharedMapDungeonIdentity.Create(dungeon.Name, entry.Root.x, entry.Root.y, entry.Root.z, gate.Path);
            return new() { new(SharedMapPinState.MarkerKey(SharedMapTableData.DungeonOwner, id),
                new MapPin(SharedMapTableData.DungeonOwner, dungeon.Label, point.x, point.y, point.z, (int)Minimap.PinType.Icon2)) };
        }
        return null;
    }
    internal List<SharedMapPersonalMarker>? Tick(float now, Vector3 centre, float radius, float portalDistance)
    {
        ReadLocation(now);
        var entered = ResolveEntry();
        if (entered != null) return entered;
        if (Character.InInterior(centre) || !SharedMapDungeonPrefab.Finite(centre) || radius <= 0 || radius > 1024)
            return null;
        if (_resourceIndex < SharedMapResources.Catalog.Length)
        {
            var type = SharedMapResources.Catalog[_resourceIndex++];
            if (!_resources.TryReuse(type.Name.GetStableHashCode(), out _) && SharedMapResourcePrefab.Read(type.Name, type.Kind, type.Drop, type.Successor) is { } resource)
                _resources.Add(resource.Hash, resource);
        }

        if (_scan == null && now < _next)
            return null;
        _scan ??= Scan(centre, radius, portalDistance).GetEnumerator();
        if (!WorkSlice.Advance(_scan, 1, 12))
            return null;
        _scan.Dispose();
        _scan = null;
        _next = now + 2;
        var result = _rows;
        _rows = new();
        return result;
    }

    private void ReadLocation(float now)
    {
        var locations = ZoneSystem.instance.m_locations;
        for (int i = 0; i < 16 && _locationIndex < locations.Count; i++)
        {
            var location = locations[_locationIndex];
            if (!location.m_prefab.IsValid)
            {
                _locationIndex++;
                continue;
            }

            string name = PrefabAssetRequest.AssetName(ref location.m_prefab);
            int hash = name.GetStableHashCode();
            if (SharedMapLandmarkPrefab.StructureLabel(name) is string label)
                _landmarks[hash] = SharedMapLandmarkPrefab.LocationRoot(name, label);
            else if (SharedMapLandmarkPrefab.Runestones.ContainsKey(name) || SharedMapDungeonPrefab.Labels.ContainsKey(name))
            {
                if (!_asset.TryGet(location.m_prefab, now, out var asset, name))
                    return;
                try
                {
                    if (asset != null && SharedMapDungeonPrefab.Labels.ContainsKey(name))
                        _dungeons[hash] = new SharedMapDungeonPrefab(asset);
                    else if (asset != null && SharedMapLandmarkPrefab.ReadRunestone(asset) is { } rune)
                        _landmarks[hash] = rune;
                }
                catch (System.IO.InvalidDataException) when (SharedMapDungeonPrefab.Labels.ContainsKey(name))
                {
                    // Like host discovery, isolate unsupported gateway layouts to their own prefab.
                }
                finally
                {
                    _asset.Dispose();
                }

                _locationIndex++;
                return;
            }

            _locationIndex++;
        }
    }

    private IEnumerable<bool> Scan(Vector3 centre, float radius, float portalDistance)
    {
        var map = new Dictionary<string, SharedMapPersonalMarker>();
        var portals = new List<MapMarker>();
        var deposits = new List<ResourceDeposit>();
        var source = new Dictionary<int, string>();
        var seen = new HashSet<ZDOID>();
        void Add(long owner, Minimap.PinType icon, string id, string label, Vector3 point)
        {
            if (Character.InInterior(point) || !SharedMapDungeonPrefab.Finite(point) || !SharedMapPersonalMarkers.Nearby(centre, point, radius))
                return;
            string key = SharedMapPinState.MarkerKey(owner, id);
            if (map.Count < SharedMapPersonalMarkers.Maximum)
                map[key] = new(key, new MapPin(owner, label, point.x, point.y, point.z, (int)icon));
        }

        var manager = ZDOMan.instance;
        // Gateway and runestone offsets can reach beyond the sector containing the actual point.
        var reach = new Vector3(radius + 128, 0, radius + 128);
        var low = ZoneSystem.GetZone(centre - reach);
        var high = ZoneSystem.GetZone(centre + reach);
        int proxy = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
        for (int x = low.x; x <= high.x; x++)
            for (int y = low.y; y <= high.y; y++)
            {
                var sector = new Vector2s(x, y);
                for (int start = 0;; start += 32)
                {
                    var records = WorldObjectProvider.BorrowPortalSector(manager, sector);
                    if (records == null || start >= records.Count) break;
                    for (int i = start; i < Math.Min(start + 32, records.Count); i++)
                    {
                        var portal = records[i];
                        if (portal == null || !portal.IsValid() || !portal.Persistent || !seen.Add(portal.m_uid)) continue;
                        var point = portal.GetPosition();
                        if (SharedMapDungeonPrefab.Finite(point) && !Character.InInterior(point))
                            ObservePortal(portal, point);
                    }
                    yield return true;
                }
                for (int start = 0;; start += 32)
                {
                    yield return true;
                    var list = WorldObjectProvider.BorrowSector(manager, sector);
                    if (list == null || start >= list.Count)
                        break;
                    for (int i = start; i < Math.Min(start + 32, list.Count); i++)
                    {
                        var z = list[i];
                        if (z == null || !z.IsValid() || !z.Persistent || !seen.Add(z.m_uid))
                            continue;
                        var point = z.GetPosition();
                        if (!SharedMapDungeonPrefab.Finite(point) || Character.InInterior(point))
                            continue;
                        int hash = z.GetPrefab();
                        if (hash == proxy)
                        {
                            int location = z.GetInt(ZDOVars.s_location);
                            if (_dungeons.TryGetValue(location, out var dungeon))
                                foreach (var g in dungeon.Gateways)
                                    Add(SharedMapTableData.DungeonOwner, Minimap.PinType.Icon2, SharedMapDungeonIdentity.Create(dungeon.Name, point.x, point.y, point.z, g.Path), dungeon.Label, point + z.GetRotation() * g.Offset);
                            if (_landmarks.TryGetValue(location, out var landmark))
                                Points(z, landmark, false);
                            continue;
                        }

                        if (Game.instance.PortalPrefabHash.Contains(hash))
                            ObservePortal(z, point);
                        else if (!SharedMapPersonalMarkers.Nearby(centre, point, radius)) continue;
                        else if (_resources.TryGetValue(hash, out var resource) && z.GetLong(ZDOVars.s_creator) == 0)
                        {
                            var progress = resource.Progress(z);
                            if (progress?.RemainingSections == 0 || (resource.Kind == ResourceKind.FlametalPillar && z.GetBool(ZDOVars.s_dead)))
                                continue;
                            // A fractured vein or lost mining sections are replicated evidence of discovery.
                            bool silver = resource.Kind == ResourceKind.Silver;
                            if (silver && !resource.Name.EndsWith("_frac", StringComparison.Ordinal) &&
                                !(progress.HasValue && progress.Value.RemainingSections < progress.Value.TotalSections))
                                continue;
                            string key = ResourceSource(resource.Kind, point.x, point.y, point.z);
                            int id = deposits.Count + 1;
                            source.Add(id, key);
                            deposits.Add(new ResourceDeposit(id, resource.Kind, point.x, point.y, point.z, progress: progress, silverDiscovered: silver, sapPercent: resource.SapPercentage(z)) { Skull = resource.SkullBounds(z) });
                        }
                        else if (PrefabRegistry.Current.TryGet(hash, out var info) && SharedMapLandmarkPrefab.PhysicalSpawnerPrefab(info.Name))
                        {
                            if (!_landmarks.TryGetValue(hash, out var spawner))
                            {
                                spawner = SharedMapLandmarkPrefab.ReadSpawner(info.Prefab, out _);
                                if (spawner == null)
                                    continue;
                                _landmarks.Add(hash, spawner);
                            }

                            Points(z, spawner, true);
                        }
                    }

                    if (list.Count <= start + 32)
                        break;
                }
            }

        void ObservePortal(ZDO portal, Vector3 position)
        {
            if (portal.GetLong(ZDOVars.s_creator) != 0 && SharedMapPersonalMarkers.Nearby(centre, position, radius + portalDistance))
                portals.Add(SharedMapPortalClusters.Observe(portal.GetPrefab(), position, portal.GetString(ZDOVars.s_tag),
                    portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone()));
        }

        void Points(ZDO record, SharedMapLandmarkPrefab meta, bool spawner)
        {
            var root = record.GetPosition();
            foreach (var p in meta.Points)
            {
                bool structure = SharedMapLandmarkPrefab.StructureLabel(meta.Name) != null;
                var owner = spawner ? SharedMapTableData.SpawnerOwner : structure ? SharedMapTableData.StructureOwner : SharedMapTableData.RunestoneOwner;
                var id = SharedMapLandmarkStore.SourceIdentity(meta.Name, root.x, root.y, root.z, p.Path, spawner);
                Add(owner, structure ? Minimap.PinType.Icon1 : Minimap.PinType.Icon3, id, p.Label, root + record.GetRotation() * p.Offset);
            }
        }

        PortalAliases.Clear();
        portals.Sort((a, b) => StringComparer.Ordinal.Compare(a.Identity, b.Identity));
        foreach (var p in SharedMapPortalClusters.Group(portals, portalDistance, (member, group) =>
            PortalAliases[SharedMapPinState.MarkerKey(SharedMapTableData.PortalOwner, member.Identity)] =
                SharedMapPinState.MarkerKey(SharedMapTableData.PortalOwner, group)))
            Add(SharedMapTableData.PortalPinOwner(p), Minimap.PinType.Icon4, p.Identity, p.Name, new(p.X, p.Y, p.Z));
        foreach (var p in SharedMapResourceClusters.Group(deposits, 0, true, pillarVerified: _ => true))
            Add(SharedMapTableData.ResourceOwner, Minimap.PinType.Icon3, source[int.Parse(p.Identity, System.Globalization.CultureInfo.InvariantCulture)], p.Name, new(p.X, p.Y, p.Z));
        _rows = map.Values.ToList();
    }

    internal static string ResourceSource(ResourceKind kind, float x, float y, float z) =>
        SharedMapPortalIdentity.Create((int)kind, x, kind == ResourceKind.FlametalPillar ? 0 : y, z);

    public void Dispose()
    {
        _scan?.Dispose();
        _asset.Dispose();
    }
}
