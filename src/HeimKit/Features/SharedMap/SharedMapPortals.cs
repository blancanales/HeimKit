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
    private ZDOID[] _portals = Array.Empty<ZDOID>();
    public int RegisteredCount => _portals.Length;
    public int EligibleCount { get; private set; }
    public SharedMapPortals(ZDOMan world, SharedMapExploration map) { _world = world; _map = map; }

    public void Refresh() => _portals = _world.GetPortalList().Where(zdo => zdo.IsValid())
        .Select(zdo => zdo.m_uid).Distinct().OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();

    public IReadOnlyList<PortalMarker> Read(float clusterDistance)
    {
        var markers = new List<PortalMarker>();
        foreach (var id in _portals)
        {
            var portal = _world.GetZDO(id);
            if (portal == null || !portal.IsValid() || !Game.instance.PortalPrefabHash.Contains(portal.GetPrefab()) ||
                portal.GetLong(ZDOVars.s_creator) == 0) continue;
            var position = portal.GetPosition();
            if (!SharedMapExploration.Finite(position.x) || !SharedMapExploration.Finite(position.y) ||
                !SharedMapExploration.Finite(position.z) || Character.InInterior(position) || !_map.IsExplored(position.x, position.z)) continue;
            string tag = portal.GetString(ZDOVars.s_tag);
            markers.Add(new PortalMarker(string.IsNullOrWhiteSpace(tag) ? "NoName" : tag, position.x, position.y, position.z, id.ToString()));
            if (markers.Count > 4096) throw new InvalidOperationException("Community portal marker limit exceeded; table data was not replaced.");
        }
        EligibleCount = markers.Count;
        return SharedMapPortalClusters.Group(markers, clusterDistance);
    }
}
