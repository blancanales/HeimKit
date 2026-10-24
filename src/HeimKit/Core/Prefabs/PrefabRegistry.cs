using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Core.Prefabs;

// One bounded, scene-scoped metadata owner. Null components are cached too.
internal sealed class PrefabRegistry
{
    // Shared headroom for the formerly independent feature caches and observed
    // location/room prefabs; feature catalog limits still apply independently.
    internal const int Capacity = 65536;
    internal static PrefabRegistry Current { get; } = new();
    private ZNetScene? _scene;
    private readonly Dictionary<int, PrefabInfo> _prefabs = new();
    private readonly Dictionary<GameObject, PrefabInfo> _objects = new();
    internal PrefabCatalog<T> Catalog<T>(string name) => new(this, name);
    internal GameObject? Prefab(int hash) => TryGet(hash, out var info) ? info.Prefab : null;
    internal GameObject? Prefab(string name) => Prefab(name.GetStableHashCode());
    internal PrefabInfo Get(int hash) => TryGet(hash, out var info) ? info : throw new InvalidOperationException("Native prefab is unavailable: " + hash);
    internal bool TryGet(int hash, out PrefabInfo info)
    {
        if (!ReferenceEquals(_scene, ZNetScene.instance)) { Clear(); _scene = ZNetScene.instance; }
        if (_prefabs.TryGetValue(hash, out info!) && info.Prefab != null) return true;
        var prefab = _scene?.GetPrefab(hash);
        if (prefab == null) { info = null!; return false; }
        // Never silently evict metadata required by an active consumer.
        if (_prefabs.Count >= Capacity) throw new InvalidOperationException("Shared prefab classification capacity exceeded.");
        info = Entry(hash); info.Prefab = prefab; Register(prefab, info); return true;
    }
    internal PrefabInfo Entry(int hash)
    {
        if (!_prefabs.TryGetValue(hash, out var info))
        {
            if (_prefabs.Count >= Capacity) throw new InvalidOperationException("Shared prefab classification capacity exceeded.");
            _prefabs.Add(hash, info = new PrefabInfo(null!));
        }
        return info;
    }
    internal void Observe(GameObject prefab)
    { if (_objects.ContainsKey(prefab)) return; var info = Entry(prefab.name.GetStableHashCode()); info.Prefab = prefab; Register(prefab, info); }
    private void Register(GameObject obj, PrefabInfo info)
    {
        _objects[obj] = info;
        foreach (Transform child in obj.transform) Register(child.gameObject, info);
    }
    internal static T? Component<T>(GameObject? obj) where T : Component
    {
        if (obj == null) return null;
        return Current._objects.TryGetValue(obj, out var info) ? info.Component<T>(obj) : obj.GetComponent<T>();
    }
    internal static T? Component<T>(Component? obj) where T : Component => Component<T>(obj == null ? null : obj.gameObject);
    internal static T[] Children<T>(GameObject obj, bool inactive = false) where T : Component =>
        Current._objects.TryGetValue(obj, out var info) ? info.Children<T>(obj, inactive) : obj.GetComponentsInChildren<T>(inactive);
    internal static T[] Children<T>(Component obj, bool inactive = false) where T : Component => Children<T>(obj.gameObject, inactive);
    internal static T[] Components<T>(Component obj) where T : Component
    {
        var root = obj.gameObject;
        if (!Current._objects.TryGetValue(root, out var info)) return root.GetComponents<T>();
        return info.Components<T>(root);
    }
    internal bool Solid(int hash) { var info = Get(hash); if (!info.SolidRead) { info.SolidRead = true; foreach (var collider in info.Children<Collider>(info.Prefab, true)) if (collider.enabled && !collider.isTrigger) { info.Solid = true; break; } } return info.Solid; }
    internal Bounds? Bounds(GameObject obj)
    {
        var info = Get(obj.name.GetStableHashCode());
        if (!info.BoundsRead) { info.BoundsRead = true; info.Bounds = PrefabCollisionBounds.Read(obj); }
        return info.Bounds;
    }
    internal void Clear() { _prefabs.Clear(); _objects.Clear(); _scene = null; }
}

internal sealed class PrefabInfo
{
    internal GameObject Prefab;
    internal readonly Dictionary<(string, Type), object?> Metadata = new();
    private readonly Dictionary<(GameObject, Type), object?> _components = new();
    private readonly Dictionary<(GameObject, Type, bool), object> _children = new();
    private readonly Dictionary<(GameObject, Type), object> _all = new();
    internal bool BoundsRead, SolidRead, Solid;
    internal Bounds? Bounds;
    internal string Name => Prefab.name;
    internal PrefabInfo(GameObject prefab) => Prefab = prefab;
    internal T? Component<T>(GameObject obj) where T : Component
    {
        var key = (obj, typeof(T));
        if (!_components.TryGetValue(key, out var value)) _components[key] = value = obj.GetComponent<T>();
        return (T?)value;
    }
    internal T[] Children<T>(GameObject obj, bool inactive) where T : Component
    {
        var key = (obj, typeof(T), inactive);
        if (!_children.TryGetValue(key, out var value)) _children[key] = value = obj.GetComponentsInChildren<T>(inactive);
        return (T[])value;
    }
    internal T? Component<T>() where T : Component => Component<T>(Prefab);
    internal T[] Components<T>(GameObject obj) where T : Component
    {
        var key = (obj, typeof(T));
        if (!_all.TryGetValue(key, out var value)) _all[key] = value = obj.GetComponents<T>();
        return (T[])value;
    }
}

// Features retain selection membership; classified values live exclusively on
// the registry's single prefab record, including negative classifications.
internal sealed class PrefabCatalog<T>
{
    private readonly PrefabRegistry _registry;
    private readonly string _name;
    private readonly HashSet<int> _selected = new();
    internal PrefabCatalog(PrefabRegistry registry, string name) { _registry = registry; _name = name; }
    internal int Count => _selected.Count;
    internal IEnumerable<int> Keys => _selected;
    internal IEnumerable<T> Values { get { foreach (var hash in _selected) yield return this[hash]; } }
    internal bool ContainsKey(int hash) => _selected.Contains(hash);
    internal bool TryGetValue(int hash, out T value)
    {
        if (_selected.Contains(hash) && _registry.Entry(hash).Metadata.TryGetValue((_name, typeof(T)), out var data))
        { value = (T)data!; return true; }
        value = default!; return false;
    }
    internal T this[int hash]
    {
        get => (T)_registry.Entry(hash).Metadata[(_name, typeof(T))]!;
        set { _registry.Entry(hash).Metadata[(_name, typeof(T))] = value; _selected.Add(hash); }
    }
    internal void Add(int hash, T value) => this[hash] = value;
    internal void Clear() => _selected.Clear();
    internal IEnumerator<KeyValuePair<int, T>> GetEnumerator()
    { foreach (var hash in _selected) yield return new KeyValuePair<int, T>(hash, this[hash]); }
}
