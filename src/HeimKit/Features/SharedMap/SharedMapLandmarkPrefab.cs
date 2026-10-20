using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapLandmarkPrefab
{
    internal static readonly IReadOnlyDictionary<string, string> Runestones = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Runestone_Boars"] = "Boar Runestone", ["Runestone_Greydwarfs"] = "Greydwarf Runestone", ["Runestone_Draugr"] = "Draugr Runestone",
        ["Runestone_Meadows"] = "Meadows Runestone", ["Runestone_BlackForest"] = "Black Forest Runestone", ["Runestone_Swamps"] = "Swamp Runestone",
        ["Runestone_Mountains"] = "Mountain Runestone", ["Runestone_Plains"] = "Plains Runestone", ["Runestone_Mistlands"] = "Mistlands Runestone",
        ["Runestone_Ashlands"] = "Ashlands Runestone", ["Runestone_DeepNorth"] = "Deep North Runestone"
    };
    private static readonly IReadOnlyDictionary<string, string> SourceLabels = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Spawner_GreydwarfNest"] = "Greydwarf Nest", ["BonePileSpawner"] = "Skeleton Bone Pile", ["BonePileSpawner_swamp"] = "Swamp Skeleton Bone Pile",
        ["Spawner_DraugrPile"] = "Draugr Pile", ["Spawner_CharredStone"] = "Charred Stone", ["Spawner_CharredStone_Elite"] = "Charred Elite Stone",
        ["Spawner_CharredStone_event"] = "Charred Event Stone", ["Spawner_CharredCross"] = "Charred Cross",
        ["BlackIce_Core"] = "Jotun Core", ["Spawner_Hole"] = "Elaking Hole", ["BlobMorkBig"] = "Mork Blob Source",
        ["Spawner_Hole_double"] = "Elaking Double Hole",
        ["EvilHeart_Forest"] = "Greydwarf Heart"
    };
    private static readonly HashSet<string> PhysicalSpawnerLabels = new(SourceLabels.Values, StringComparer.Ordinal);
    internal static bool PhysicalSpawnerLabel(string label) => PhysicalSpawnerLabels.Contains(label);
    internal static bool PhysicalSpawnerPrefab(string name) => SourceLabels.ContainsKey(name);
    public string Name { get; }
    public (string Path, string Label, Vector3 Offset)[] Points { get; }
    public float MaximumOffset { get; }
    public Vector3 Scale { get; private set; } = Vector3.one;
    public bool SyncScale { get; private set; }
    private SharedMapLandmarkPrefab(string name, List<(string Path, string Label, Vector3 Offset)> points)
    {
        Name = name; Points = points.ToArray();
        MaximumOffset = points.Count == 0 ? 0 : points.Max(p => Mathf.Sqrt(p.Offset.x * p.Offset.x + p.Offset.z * p.Offset.z));
    }
    // Root rotation is removed while retaining scale, matching ZoneSystem.SpawnLocation's root normalization.
    internal static Vector3 Offset(Transform root, Transform point) => Quaternion.Inverse(root.rotation) * (point.position - root.position);
    private static string Path(Transform root, Transform point)
    {
        var path = new List<string>();
        for (var node = point; node != root; node = node.parent)
        { if (node == null) throw new InvalidOperationException("Source is outside its prefab."); path.Insert(0, node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture)); }
        return path.Count == 0 ? "root" : string.Join("/", path);
    }
    private static bool Active(Transform root, Transform point, bool rune)
    {
        for (var node = point; node != null; node = node.parent)
        {
            // Asset roots may be inactive for soft-reference storage; descendants must be active.
            if (node != root && !node.gameObject.activeSelf) return false;
            foreach (var behaviour in node.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) return false;
                if (!behaviour.enabled) continue;
                if (behaviour is RandomSpawn || behaviour is RandomObject || behaviour is SnapToGround) return false;
                if (rune && !(behaviour is RuneStone) && !(behaviour is Location) && !(behaviour is LodFadeInOut) && !(behaviour is StaticPhysics)) return false;
                if (!rune && !(behaviour is ZNetView) && !(behaviour is SpawnArea) && !(behaviour is CreatureSpawner) &&
                    !(behaviour is Destructible) && !(behaviour is StaticPhysics) && !(behaviour is LodFadeInOut) &&
                    behaviour.GetType().Name != "HoverText" && behaviour.GetType().Name != "DropOnDestroyed" &&
                    behaviour.GetType().Name != "TriggerPersistentEventOnDestroy") return false;
            }
            if (node.GetComponents<Component>().Any(c => c == null || c.GetType().Name == "Animator" || c.GetType().Name == "Animation")) return false;
            if (node == root) return true;
        }
        return false;
    }
    public static SharedMapLandmarkPrefab? ReadRunestone(GameObject prefab)
    {
        if (!Runestones.TryGetValue(prefab.name, out string label) || prefab.GetComponent<Location>() == null) return null;
        var points = new List<(string, string, Vector3)>();
        foreach (var stone in prefab.GetComponentsInChildren<RuneStone>(true))
        {
            if (!stone.enabled || stone.GetComponent<ZNetView>() != null || stone.GetComponent<Destructible>() != null ||
                stone.GetComponent<LodFadeInOut>() == null || stone.GetComponent<StaticPhysics>() == null ||
                stone.GetComponent<Vegvisir>() != null || !Active(prefab.transform, stone.transform, true)) continue;
            Vector3 offset = Offset(prefab.transform, stone.transform);
            if (!SharedMapDungeonPrefab.Finite(offset) || Math.Abs(offset.y) >= 1000 || offset.sqrMagnitude > 128 * 128) continue;
            points.Add((Path(prefab.transform, stone.transform), label, offset));
        }
        return points.Count == 0 || points.Count > 16 ? null : new SharedMapLandmarkPrefab(prefab.name, points);
    }
    internal static bool Repeating(float interval) => SharedMapExploration.Finite(interval) && interval > 0;
    private static bool PhysicalPartActive(Transform root, Transform part)
    {
        for (var node = part; node != null; node = node.parent)
        {
            if (node != root && !node.gameObject.activeSelf) return false;
            // Decorative flicker/animation and an Aoe on a physical hitbox do not
            // turn a destroyable source into a hidden population instruction.
            foreach (var behaviour in node.GetComponents<MonoBehaviour>())
                if (behaviour == null || behaviour.enabled && (behaviour is RandomSpawn || behaviour is RandomObject || behaviour is SnapToGround)) return false;
            if (node == root) return true;
        }
        return false;
    }
    private static bool OwnedByRoot(Transform root, Transform part, ZNetView view)
    {
        // GetComponentInParent without includeInactive can miss a stored inactive
        // root. Walk explicitly, stopping at the nearest view and prefab boundary.
        for (var node = part; node != null; node = node.parent)
        {
            var owner = node.GetComponent<ZNetView>();
            if (owner != null) return owner == view;
            if (node == root) return false;
        }
        return false;
    }
    public static SharedMapLandmarkPrefab? ReadSpawner(GameObject prefab, out string rejection)
    {
        rejection = "outside_physical_allowlist";
        if (!SourceLabels.TryGetValue(prefab.name, out string label)) return null;
        rejection = "missing_or_invalid_destructible";
        var destructible = prefab.GetComponent<Destructible>();
        if (destructible == null || !destructible.enabled || !SharedMapExploration.Finite(destructible.m_health) || destructible.m_health <= 0) return null;
        var view = prefab.GetComponent<ZNetView>();
        rejection = "unsupported_root_metadata";
        if (view == null || !view.m_persistent || prefab.GetComponent<Character>() != null || prefab.GetComponent<ItemDrop>() != null ||
            prefab.GetComponent<Piece>() != null || prefab.GetComponent<Rigidbody>() is Rigidbody body && !body.isKinematic) return null;
        // The allowlist is audited against physical game assets. Rendering data is
        // not a server-side eligibility signal; retain actual damage/collision metadata.
        bool hittable = prefab.GetComponentsInChildren<Collider>(true).Any(c => c.enabled && !c.isTrigger &&
            OwnedByRoot(prefab.transform, c.transform, view) && PhysicalPartActive(prefab.transform, c.transform));
        rejection = "missing_owned_hit_collider";
        if (!hittable) return null;
        var points = new List<(string, string, Vector3)>();
        var transforms = new HashSet<Transform>();
        void Add(Behaviour source, IEnumerable<GameObject> creatures)
        {
            if (!source.enabled || !Active(prefab.transform, source.transform, false) || !transforms.Add(source.transform)) return;
            var eligible = creatures.Where(c => c != null).ToArray();
            if (eligible.Length == 0 || eligible.Any(c => c.GetComponent<Character>() == null && c.GetComponent<Fish>() == null)) return;
            Vector3 offset = Offset(prefab.transform, source.transform);
            if (label.Contains("$") || System.Text.Encoding.UTF8.GetByteCount(label) > 128 || !SharedMapDungeonPrefab.Finite(offset) || offset.sqrMagnitude > 128 * 128) return;
            points.Add((Path(prefab.transform, source.transform), label, offset));
        }
        foreach (var area in prefab.GetComponentsInChildren<SpawnArea>(true))
            if (OwnedByRoot(prefab.transform, area.transform, view) && Repeating(area.m_spawnIntervalSec) && area.m_maxNear > 0 && area.m_maxTotal > 0 &&
                area.m_prefabs.Count > 0 && area.m_prefabs.All(p => p.m_prefab != null && SharedMapExploration.Finite(p.m_weight) && p.m_weight > 0))
                Add(area, area.m_prefabs.Select(p => p.m_prefab));
        foreach (var source in prefab.GetComponentsInChildren<CreatureSpawner>(true))
            if (source.GetComponent<ZNetView>() == view && Repeating(source.m_respawnTimeMinuts) && source.m_creaturePrefab != null)
                Add(source, new[] { source.m_creaturePrefab });
        var scale = prefab.transform.localScale;
        rejection = "invalid_scale";
        if (!SharedMapDungeonPrefab.Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0) return null;
        rejection = "no_supported_recurring_source";
        if (points.Count == 0 || points.Count > 64) return null;
        rejection = "";
        return new SharedMapLandmarkPrefab(prefab.name, points) { Scale = scale, SyncScale = view.m_syncInitialScale };
    }
}
