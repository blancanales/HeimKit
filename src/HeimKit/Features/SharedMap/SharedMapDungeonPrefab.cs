using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapDungeonPrefab
{
    internal static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Crypt2"] = "Burial Chamber", ["Crypt3"] = "Burial Chamber", ["Crypt4"] = "Burial Chamber",
        ["SunkenCrypt4"] = "Sunken Crypt", ["TrollCave02"] = "Troll Cave", ["MountainCave02"] = "Frost Cave",
        ["Mistlands_DvergrTownEntrance1"] = "Infested Mine", ["Mistlands_DvergrTownEntrance2"] = "Infested Mine",
        ["Mistlands_DvergrBossEntrance1"] = "Infested Citadel", ["Hildir_crypt"] = "Smouldering Tomb",
        ["Hildir_cave"] = "Howling Cavern", ["MorgenHole1"] = "Putrid Hole", ["MorgenHole2"] = "Putrid Hole",
        ["MorgenHole3"] = "Putrid Hole", ["PlaceofMystery3"] = "Reto's Tomb", ["BearCave"] = "Bear Cave",
        ["TheHole01"] = "Winding Tunnels", ["MorkBorg"] = "Mörkhalla"
    };
    public string Name { get; }
    public string Label { get; }
    public IReadOnlyList<DungeonGateway> Gateways { get; }
    public float MaximumOffset { get; }
    public (int Hash, Vector3 Offset, string Item)[] Pickups { get; }
    public int[] Generators { get; }
    public (int Hash, Vector3 Offset)[] GeneratorPoints { get; }
    public bool CustomInterior { get; }
    public Vector3 CustomInteriorShift { get; }
    public (int Hash, Vector3 Offset, string Item)[] CustomPickups { get; }

    public SharedMapDungeonPrefab(GameObject prefab)
    {
        Name = prefab.name;
        if (!Labels.TryGetValue(Name, out string label)) throw new InvalidDataException("Unsupported dungeon prefab.");
        Label = label;
        var location = prefab.GetComponent<Location>();
        if (location == null || !location.m_hasInterior) throw new InvalidDataException("Dungeon has no native interior metadata.");
        CustomInterior = location.m_useCustomInteriorTransform && location.m_interiorTransform != null && location.m_generator != null;
        CustomInteriorShift = CustomInterior ? location.m_interiorTransform!.localPosition + location.m_generator!.transform.localPosition : Vector3.zero;
        if (CustomInterior) CustomInteriorShift = new Vector3(CustomInteriorShift.x, location.m_interiorTransform!.localPosition.y, CustomInteriorShift.z);
        Pickups = SharedMapDungeonLoot.Points(prefab, CustomInterior ? location.m_interiorTransform : null);
        CustomPickups = CustomInterior ? SharedMapDungeonLoot.Points(location.m_interiorTransform!.gameObject) : Array.Empty<(int, Vector3, string)>();
        GeneratorPoints = prefab.GetComponentsInChildren<DungeonGenerator>(true).Select(g =>
            (Utils.GetPrefabName(g.gameObject).GetStableHashCode(), Quaternion.Inverse(prefab.transform.rotation) * (g.transform.position - prefab.transform.position))).ToArray();
        Generators = GeneratorPoints.Select(g => g.Hash).Distinct().ToArray();
        var gateways = new List<DungeonGateway>();
        var root = prefab.transform;
        foreach (var teleport in prefab.GetComponentsInChildren<Teleport>(true))
        {
            var target = teleport.m_targetPoint;
            if (!teleport.enabled || teleport.m_hoverText != "$location_enter" || target == null || target.m_targetPoint != teleport ||
                target.m_hoverText != "$location_exit" || !target.transform.IsChildOf(root)) continue;
            Vector3 offset = Quaternion.Inverse(root.rotation) * (teleport.transform.position - root.position);
            Vector3 exit = Quaternion.Inverse(root.rotation) * (target.transform.position - root.position);
            if (!Finite(offset) || !Finite(exit) || Math.Abs(offset.y) >= 1000 || exit.y < 4000) continue;
            var indices = new List<string>();
            bool valid = true;
            for (var node = teleport.transform; node != null; node = node.parent)
            {
                if (!node.gameObject.activeSelf) { valid = false; break; }
                // Reject behaviours that can conditionally move, randomize or deactivate the gateway ancestry.
                foreach (var behaviour in node.GetComponents<MonoBehaviour>())
                    if (behaviour == null || behaviour != teleport && behaviour != location && behaviour.enabled) valid = false;
                if (node == root) break;
                indices.Insert(0, node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
            }
            if (!valid) continue;
            string path = string.Join("/", indices);
            if (path.Length == 0 || path.Length > 512 || gateways.Count >= 16) throw new InvalidDataException("Unsupported dungeon gateway hierarchy.");
            float radius = Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
            if (!SharedMapExploration.Finite(radius) || radius > 128) throw new InvalidDataException("Unsupported dungeon exterior offset.");
            MaximumOffset = Math.Max(MaximumOffset, radius);
            gateways.Add(new DungeonGateway(path, offset, exit));
        }
        if (gateways.Count == 0) throw new InvalidDataException("No validated static exterior gateway.");
        Gateways = gateways;
    }

    internal static bool Finite(Vector3 p) => SharedMapExploration.Finite(p.x) && SharedMapExploration.Finite(p.y) && SharedMapExploration.Finite(p.z);
}
