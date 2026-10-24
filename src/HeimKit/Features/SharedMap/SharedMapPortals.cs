using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Native portal records persist with the world; no scene loads or network ownership changes are needed.
internal sealed class SharedMapPortals
{
    private readonly ZDOMan _world;
    private readonly SharedMapExploration _map;
    private readonly List<ZDOID> _portals = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly Dictionary<ZDOID, (int Prefab, string Tag, MapMarker Marker)> _observed = new();
    private readonly List<ZDOID> _expired = new();
    private readonly List<MapMarker> _markers = new(), _unique = new();
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private static readonly Comparison<ZDOID> IdOrder = NativeIdentity.CompareOrdinal;
    private static readonly Comparison<MapMarker> MarkerOrder = (a, b) => StringComparer.Ordinal.Compare(a.Identity, b.Identity);
    public int RegisteredCount => _portals.Count;
    public int EligibleCount { get; private set; }
    private IReadOnlyList<MapMarker>? _grouped;
    private List<MapMarker>? _previous;
    private float _cluster;
    public SharedMapPortals(ZDOMan world, SharedMapExploration map) { _world = world; _map = map; }

    public void Refresh()
    {
        _portals.Clear(); _seen.Clear();
        foreach (var record in _world.GetPortalList()) if (record.IsValid() && _seen.Add(record.m_uid)) _portals.Add(record.m_uid);
        _portals.Sort(IdOrder);
        _expired.Clear(); foreach (var id in _observed.Keys) if (!_seen.Contains(id)) _expired.Add(id);
        foreach (var id in _expired) _observed.Remove(id);
    }

    public IReadOnlyList<MapMarker> Read(float clusterDistance)
    {
        var markers = _markers; markers.Clear();
        foreach (var id in _portals)
        {
            var portal = _world.GetZDO(id);
            if (portal == null || !portal.IsValid() || !Game.instance.PortalPrefabHash.Contains(portal.GetPrefab()) ||
                portal.GetLong(ZDOVars.s_creator) == 0) continue;
            var position = portal.GetPosition();
            if (!SharedMapExploration.Finite(position.x) || !SharedMapExploration.Finite(position.y) ||
                !SharedMapExploration.Finite(position.z) || Character.InInterior(position) || !_map.IsExplored(position.x, position.z)) continue;
            string tag = portal.GetString(ZDOVars.s_tag);
            if (!_observed.TryGetValue(id, out var saved) || saved.Prefab != portal.GetPrefab() || saved.Tag != tag ||
                saved.Marker.X != position.x || saved.Marker.Y != position.y || saved.Marker.Z != position.z)
            {
                saved = (portal.GetPrefab(), tag, new MapMarker(string.IsNullOrWhiteSpace(tag) ? "NoName" : tag, position.x, position.y, position.z,
                    SharedMapPortalIdentity.Create(portal.GetPrefab(), position.x, position.y, position.z)));
                if (_observed.Count >= 4096 && !_observed.ContainsKey(id)) throw new InvalidOperationException("Community portal marker limit exceeded; table data was not replaced.");
                _observed[id] = saved;
            }
            markers.Add(saved.Marker);
            if (markers.Count > 4096) throw new InvalidOperationException("Community portal marker limit exceeded; table data was not replaced.");
        }
        if (_grouped != null && _cluster == clusterDistance && _previous != null && SharedMapReconciliationState.SameMarkers(markers, _previous)) return _grouped;
        _previous ??= new List<MapMarker>(); _previous.Clear(); _previous.AddRange(markers); _cluster = clusterDistance;
        // Ambiguous overlapping records do not acquire an arbitrary persistent identity.
        _identities.Clear();
        foreach (var marker in markers) { _identities.TryGetValue(marker.Identity, out int count); _identities[marker.Identity] = count + 1; }
        _unique.Clear(); foreach (var marker in markers) if (_identities[marker.Identity] == 1) _unique.Add(marker);
        _unique.Sort(MarkerOrder);
        // The cluster output may retain its input when distance is zero.
        var unique = new MapMarker[_unique.Count]; _unique.CopyTo(unique);
        EligibleCount = unique.Length;
        return _grouped = SharedMapPortalClusters.Group(unique, clusterDistance);
    }
}
