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
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapDungeonLoot
{
    private sealed class Target
    {
        public SharedMapDungeonPrefab Prefab = null!;
        public Vector3 Root, Interior;
        public Quaternion Rotation;
        public readonly HashSet<string> Gateways = new();
        public readonly ScheduleToken Next = new();
        public float EmptySince = -1;
    }
    private readonly ZDOMan _world;
    private readonly SharedMapDungeonStore _store;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly Dictionary<string, Target> _targets = new();
    private readonly Dictionary<(string Name, Vector3 Root), string> _targetIdentities = new();
    private readonly List<string> _order = new();
    private readonly PrefabCatalog<(int Hash, Vector3 Offset, string Item)[]> _rooms = PrefabRegistry.Current.Catalog<(int Hash, Vector3 Offset, string Item)[]>("SharedMapDungeonLoot._rooms");
    private readonly Dictionary<ZDOID, string> _claims = new();
    private readonly List<ZDO> _near = new(), _objects = new();
    private readonly List<(int Hash, Vector3 Position)> _points = new();
    private readonly Dictionary<(int Hash, int X, int Y, int Z), List<Vector3>> _cells = new();
    private readonly Stack<List<Vector3>> _cellLists = new();
    private readonly HashSet<int> _pickupHashes = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly HashSet<(int Hash, float X, float Y, float Z)> _matchedSites = new();
    private int _cursor, _changed;
    public SharedMapDungeonLoot(ZDOMan world, SharedMapDungeonStore store, SharedMapDiagnostics diagnostics)
    { _world = world; _store = store; _diagnostics = diagnostics; }

    internal static bool Objective(string location, string item) => location switch
    {
        "Crypt2" or "Crypt3" or "Crypt4" or "Hildir_crypt" => item == "SurtlingCore",
        "Mistlands_DvergrTownEntrance1" or "Mistlands_DvergrTownEntrance2" => item == "BlackCore",
        "MountainCave02" or "Hildir_cave" => item == "WolfHairBundle",
        "TheHole01" => item == "FrostCore",
        "MorkBorg" => item.StartsWith("AncientGemstone", StringComparison.Ordinal),
        _ => false
    };
    internal static (int Hash, Vector3 Offset, string Item)[] Points(GameObject root, Transform? exclude = null)
    {
        PrefabRegistry.Current.Observe(root);
        var output = new List<(int, Vector3, string)>();
        foreach (var pickup in PrefabRegistry.Children<Pickable>(root, true))
        {
            if (exclude != null && pickup.transform.IsChildOf(exclude)) continue;
            var view = PrefabRegistry.Component<ZNetView>(pickup);
            if (!pickup.enabled || pickup.m_defaultPicked || view == null || !view.m_persistent || pickup.m_itemPrefab == null || pickup.m_hideWhenPicked == null || pickup.m_respawnTimeMinutes != 0) continue;
            Vector3 offset = Quaternion.Inverse(root.transform.rotation) * (pickup.transform.position - root.transform.position);
            if (!SharedMapDungeonPrefab.Finite(offset)) continue;
            output.Add((Utils.GetPrefabName(pickup.gameObject).GetStableHashCode(), offset, pickup.m_itemPrefab.name));
            if (output.Count > 4096) throw new InvalidDataException("Dungeon pickup template limit exceeded.");
        }
        return output.ToArray();
    }
    public void Track(string gateway, SharedMapDungeonPrefab prefab, Vector3 root, Quaternion rotation, Vector3 interior)
    {
        var source = (prefab.Name, root);
        if (!_targetIdentities.TryGetValue(source, out string key))
        {
            if (_targetIdentities.Count >= 4096) throw new InvalidDataException("Dungeon pickup target limit exceeded.");
            key = SharedMapDungeonIdentity.Create(prefab.Name, root.x, root.y, root.z, "loot"); _targetIdentities.Add(source, key);
        }
        if (!_targets.TryGetValue(key, out var target))
        {
            if (_targets.Count >= 4096) throw new InvalidDataException("Dungeon pickup target limit exceeded.");
            target = new Target { Prefab = prefab, Root = root, Rotation = rotation, Interior = interior };
            _targets.Add(key, target); _order.Add(key);
        }
        target.Gateways.Add(gateway);
    }
    public void Tick(float now, float interval)
    {
        if (_order.Count == 0) return;
        string key = _order[_cursor++ % _order.Count]; if (_cursor == int.MaxValue) _cursor = 0;
        var target = _targets[key]; if (!target.Next.Ready(now)) return;
        var points = _points; points.Clear();
        foreach (var point in target.Prefab.Pickups)
            if (Objective(target.Prefab.Name, point.Item)) points.Add((point.Hash, target.Root + target.Rotation * point.Offset));
        Vector3 customOrigin = ZoneSystem.GetZonePos(ZoneSystem.GetZone(target.Root)) + target.Prefab.CustomInteriorShift;
        customOrigin.y = target.Root.y + target.Prefab.CustomInteriorShift.y;
        foreach (var point in target.Prefab.CustomPickups)
            if (Objective(target.Prefab.Name, point.Item)) points.Add((point.Hash, customOrigin + point.Offset));
        Vector3 interior = target.Prefab.CustomInterior ? customOrigin : target.Interior;
        _near.Clear();
        WorldObjectProvider.Current.For("SharedMap").Query(ZoneSystem.GetZone(interior), new SimulationDistance(2, 0, true), _near);
        if (_near.Count > 65536) { Unknown(target, now, "interior_record_limit"); return; }
        ZDO? generator = null;
        int generatorCount = 0;
        foreach (var zdo in _near)
        {
            if (!zdo.IsValid() || !zdo.Persistent) continue;
            foreach (var point in target.Prefab.GeneratorPoints)
                if (point.Hash == zdo.GetPrefab() &&
                    (zdo.GetPosition() - (target.Prefab.CustomInterior ? customOrigin : target.Root + target.Rotation * point.Offset)).sqrMagnitude <= .01f)
                { generator = zdo; generatorCount++; break; }
        }
        if (target.Prefab.Generators.Length > 0)
        {
            if (generatorCount != 1) { Unknown(target, now, "layout_not_unique_or_unavailable"); return; }
            byte[]? data = generator!.GetByteArray(ZDOVars.s_roomData);
            bool valid = SharedMapDungeonLayout.TryRead(data, out var rooms, out string reason);
            _diagnostics.DungeonLayout(now, target.Gateways.First(), target.Prefab.Name, target.Root, data?.Length ?? 0,
                data != null && data.Length >= 4 ? BitConverter.ToInt32(data, 0) : 0, valid, reason);
            if (!valid) { Unknown(target, now, reason); return; }
            foreach (var placement in rooms)
            {
                int hash = placement.Hash; Vector3 position = placement.Position;
                Quaternion rotation = Quaternion.Euler(placement.RotationEuler);
                if (!_rooms.TryGetValue(hash, out var template))
                {
                    var room = DungeonDB.instance.GetRoom(hash);
                    if (room == null || _rooms.Count >= 2048) { Unknown(target, now, "room_metadata_unavailable"); return; }
                    var reference = room.m_prefab;
                    try { reference.Load(); if (reference.Asset == null) { Unknown(target, now, "room_asset_unavailable"); return; } _rooms[hash] = Points(reference.Asset); }
                    finally { reference.Release(); }
                    return; // At most one newly loaded room template per occupied tick.
                }
                foreach (var point in template)
                    if (Objective(target.Prefab.Name, point.Item)) points.Add((point.Hash, position + rotation * point.Offset));
                if (points.Count > 65536) { Unknown(target, now, "pickup_point_limit"); return; }
            }
        }
        target.Next.Postpone(now, interval);
        if (points.Count == 0) { Record(target, now, 0, 0, "verified_empty_layout", verifiedEmpty: true); return; }
        foreach (var list in _cells.Values)
        {
            list.Clear();
            if (_cellLists.Count < 4096 && list.Capacity <= 64) _cellLists.Push(list);
        }
        _cells.Clear(); _pickupHashes.Clear();
        foreach (var point in points)
        {
            var cell = Cell(point.Hash, point.Position);
            if (!_cells.TryGetValue(cell, out var list))
            { list = _cellLists.Count > 0 ? _cellLists.Pop() : new List<Vector3>(); _cells.Add(cell, list); }
            list.Add(point.Position); _pickupHashes.Add(point.Hash);
        }
        Vector3 centre = interior; float zoneSize = ZoneSystem.instance.m_zoneSize;
        float extent = points.Max(p => Math.Max(Math.Abs(p.Position.x - centre.x), Math.Abs(p.Position.z - centre.z)));
        int radius = (int)Math.Ceiling(extent / zoneSize) + 1;
        if (radius > 16) { Unknown(target, now, "pickup_extent_limit"); return; }
        _objects.Clear(); WorldObjectProvider.Current.For("SharedMap").Query(ZoneSystem.GetZone(centre), new SimulationDistance(radius, 0, true), _objects);
        if (_objects.Count > 65536) { Unknown(target, now, "pickup_record_limit"); return; }
        int total = 0, picked = 0; _seen.Clear(); _matchedSites.Clear();
        foreach (var zdo in _objects)
        {
            if (!zdo.IsValid() || !zdo.Persistent || !_pickupHashes.Contains(zdo.GetPrefab()) || !_seen.Add(zdo.m_uid)) continue;
            var position = zdo.GetPosition(); if (!SharedMapDungeonPrefab.Finite(position)) continue;
            int matches = FindSite(_cells, zdo.GetPrefab(), position, out var site);
            if (matches == 0) continue;
            if (matches != 1 || !_matchedSites.Add(site))
            { Unknown(target, now, "ambiguous_pickup_site"); return; }
            if (_claims.TryGetValue(zdo.m_uid, out string owner) && owner != key)
            { Unknown(_targets[owner], now, "ambiguous_pickup_membership"); Unknown(target, now, "ambiguous_pickup_membership"); return; }
            if (_claims.Count >= 65536 && !_claims.ContainsKey(zdo.m_uid)) { Unknown(target, now, "pickup_identity_limit"); return; }
            _claims[zdo.m_uid] = key; total++; if (zdo.GetBool(ZDOVars.s_picked)) picked++;
        }
        // Random room spawns can leave candidate sites with no actual Pickable records.
        // Wait across complete scans after a visit; absent formerly established pickups stay unknown.
        if (total == 0)
        {
            bool visited = false; foreach (var gateway in target.Gateways) if (_store.Visited(gateway)) { visited = true; break; }
            if (!visited)
            { Record(target, now, 0, 0, "unvisited_empty_inventory"); return; }
            if (target.EmptySince < 0) target.EmptySince = now;
            if (now - target.EmptySince < 30)
            { Pending(target, now); return; }
        }
        else target.EmptySince = -1;
        Record(target, now, total, picked, total == 0 ? "observed_empty_inventory" : "saved_pickup_flags", verifiedEmpty: total == 0);
    }
    private void Record(Target target, float now, int total, int picked, string reason, bool verifiedEmpty = false)
    {
        foreach (string gateway in target.Gateways)
        {
            bool changed = _store.Progress(gateway, total, picked, verifiedEmpty); if (changed) _changed++;
            if (_store.VisitFromPickups(gateway, total, picked))
                _diagnostics.DungeonPriorVisit(now, gateway, total, picked);
            var accepted = _store.ProgressState(gateway);
            _diagnostics.DungeonLoot(now, gateway, target.Prefab.Name, target.Root, accepted.Total, accepted.Picked, changed,
                accepted.Picked < 0 ? "missing_or_unestablished_pickups" : reason);
        }
    }
    // An empty scan inside the wait is inconclusive, so established progress and its label stay published.
    private void Pending(Target target, float now)
    {
        foreach (string gateway in target.Gateways)
        {
            var accepted = _store.ProgressState(gateway);
            _diagnostics.DungeonLoot(now, gateway, target.Prefab.Name, target.Root, accepted.Total, accepted.Picked, false, "empty_inventory_pending");
        }
    }
    internal static int FindSite(IReadOnlyDictionary<(int Hash, int X, int Y, int Z), List<Vector3>> cells,
        int hash, Vector3 position, out (int Hash, float X, float Y, float Z) site)
    {
        var cell = Cell(hash, position);
        site = default;
        bool found = false, ambiguous = false;
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            if (cells.TryGetValue((cell.Hash, cell.X + x, cell.Y + y, cell.Z + z), out var expected))
                foreach (var point in expected)
                    if ((point - position).sqrMagnitude <= .01f)
                    {
                        var match = (cell.Hash, point.x, point.y, point.z);
                        if (found && !site.Equals(match)) ambiguous = true;
                        site = match; found = true;
                    }
        return ambiguous ? 2 : found ? 1 : 0;
    }
    private static (int Hash, int X, int Y, int Z) Cell(int hash, Vector3 p) => (hash, (int)Math.Floor(p.x), (int)Math.Floor(p.y), (int)Math.Floor(p.z));
    private void Unknown(Target target, float now, string reason)
    {
        target.EmptySince = -1;
        target.Next.Postpone(now, 10);
        foreach (string gateway in target.Gateways)
        {
            bool changed = _store.Progress(gateway, 0, 0); if (changed) _changed++;
            var accepted = _store.ProgressState(gateway);
            _diagnostics.DungeonLoot(now, gateway, target.Prefab.Name, target.Root, accepted.Total, -1, changed, reason);
        }
    }
    public int TakeChanges() { int count = _changed; _changed = 0; return count; }
}
