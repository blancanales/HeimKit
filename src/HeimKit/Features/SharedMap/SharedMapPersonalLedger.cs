using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Native;
using HeimKit.Features.SharedMap.Models;
using HarmonyLib;
using UnityEngine;

namespace HeimKit.Features.SharedMap;
internal sealed class SharedMapPersonalLedger : IDisposable
{
    private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> Pins = NativeAccess.Field<Minimap, List<Minimap.PinData>>("m_pins");
    private readonly Minimap _map;
    private readonly long _world, _profile;
    private readonly SnapshotFile _file;
    private readonly Dictionary<string, SharedMapPersonalMarker> _known = new();
    private readonly Dictionary<string, Minimap.PinData> _overlay = new();
    internal readonly SharedMapClientPortalStatus PortalStatus = new();
    private bool _dirty, _hasLocalPolicy;
    internal SharedMapPersonalLedger(string directory, Minimap map, long world, long profile)
    {
        _map = map;
        _world = world;
        _profile = profile;
        string path = Path.Combine(directory, "SharedMap", $"{world:X16}-{profile:X16}.personal");
        _file = new(path);
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak"))
                throw new InvalidDataException("Personal marker file is missing beside its backup.");
            Draw();
            return;
        }

        var length = new FileInfo(path).Length;
        if (length < 32 || length > SharedMapPersonalMarkers.Bytes + 32)
            throw new InvalidDataException("Invalid personal marker file size.");
        byte[] raw = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(raw, 0, raw.Length - 32);
        if (!raw.AsSpan(raw.Length - 32).SequenceEqual(hash))
            throw new InvalidDataException("Personal marker checksum mismatch.");
        Array.Resize(ref raw, raw.Length - 32);
        foreach (var row in SharedMapPersonalMarkers.Decode(raw, world, profile, 0, out _, out _))
            _known.Add(row.Id, row);
        Draw();
    }

    private static bool Same(MapPin a, MapPin b) => a.Owner == b.Owner && a.Name == b.Name && a.Icon == b.Icon && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Checked == b.Checked;
    private static (string, int, Vector3) Shape(MapPin p) => (p.Name, p.Icon, new Vector3(p.X, p.Y, p.Z));
    private static (string, int, Vector3) Shape(Minimap.PinData p) => (p.m_name, (int)p.m_type, p.m_pos);
    private Minimap.PinData Add(MapPin p, bool save) => _map.AddPin(new Vector3(p.X, p.Y, p.Z), (Minimap.PinType)p.Icon, p.Name, save, p.Checked, p.Owner);
    private void RemoveOverlay(string id)
    {
        if (!_overlay.TryGetValue(id, out var pin))
            return;
        if (_map != null)
            _map.RemovePin(pin);
        _overlay.Remove(id);
    }

    internal (int Added, int Updated, int Retired) Apply(List<SharedMapPersonalMarker> rows, Vector3 centre, float radius, bool authoritative, Action<SharedMapPersonalMarker>? onAdded = null)
    {
        var incoming = rows.ToDictionary(r => r.Id);
        var retired = authoritative ? _known.Values.Where(r => SharedMapPersonalMarkers.Nearby(r.Position, centre, radius) && !r.PendingLocal && !incoming.ContainsKey(r.Id)).ToArray() : Array.Empty<SharedMapPersonalMarker>();
        int added = 0, updated = 0;
        var oldSaved = new Dictionary<(string, int, Vector3), List<Minimap.PinData>>();
        void IndexSaved()
        {
            foreach (var pin in Pins(_map))
            {
                if (!pin.m_save || !SharedMapTableData.ManagedOwner(pin.m_ownerID))
                    continue;
                var key = Shape(pin);
                if (!oldSaved.TryGetValue(key, out var list))
                    oldSaved.Add(key, list = new());
                list.Add(pin);
            }
        }

        bool indexed = false;
        bool RemoveFormer(MapPin former)
        {
            if (!indexed)
            {
                IndexSaved();
                indexed = true;
            }

            if (!oldSaved.TryGetValue(Shape(former), out var list))
                return false;
            bool removed = false;
            foreach (var p in list)
                if (p.m_ownerID == former.Owner)
                {
                    _map.RemovePin(p);
                    removed = true;
                }

            oldSaved.Remove(Shape(former));
            return removed;
        }

        foreach (var old in retired)
        {
            RemoveOverlay(old.Id);
            RemoveFormer(old.Pin);
            _known.Remove(old.Id);
        }

        foreach (var row in rows)
        {
            if (_known.TryGetValue(row.Id, out var old))
            {
                // Re-observing the physical source must not overwrite host progress,
                // checkmarks, publication offsets, or an adopted pin.
                if (row.PendingLocal && !old.PendingLocal) continue;
                if (old.PendingLocal && !row.PendingLocal) { old.PendingLocal = false; _dirty = true; }
                row.Adopted = old.Adopted;
                if (Same(old.Pin, row.Pin) && !old.TableOnly)
                    continue;
                RemoveOverlay(old.Id);
                if (RemoveFormer(old.Pin) && !row.Adopted)
                    Add(row.Pin, true);
                updated++;
            }
            else
            {
                if (_known.Count >= SharedMapPersonalMarkers.Maximum)
                    continue;
                added++;
                onAdded?.Invoke(row);
            }

            _known[row.Id] = row;
        }

        if (added + updated + retired.Length > 0)
        {
            _dirty = true;
            Draw();
        }

        return (added, updated, retired.Length);
    }

    internal void ReconcileAliases(Dictionary<string, string> aliases, bool pendingOnly = false)
    {
        foreach (var row in _known.Values.Where(r => aliases.ContainsKey(r.Id) && !r.TableOnly && (!pendingOnly || r.PendingLocal)).ToArray())
        {
            string group = aliases[row.Id];
            RemoveOverlay(row.Id);
            _known.Remove(row.Id);
            if (!_known.ContainsKey(group))
                _known.Add(group, new(group, row.Pin) { PendingLocal = true, Adopted = row.Adopted });
            _dirty = true;
        }
    }

    internal void SetLocalPolicy(Func<SharedMapPersonalMarker, bool> allowed)
    {
        _hasLocalPolicy = true;
        foreach (var row in _known.Values.Where(r => r.PendingLocal && !allowed(r)).ToArray())
        {
            RemoveOverlay(row.Id);
            _known.Remove(row.Id);
            _dirty = true;
        }
        Draw();
    }

    internal List<Minimap.PinData> Detach()
    {
        var pins = Pins(_map);
        var held = pins.Where(p => SharedMapTableData.ManagedOwner(p.m_ownerID)).ToList();
        pins.RemoveAll(p => SharedMapTableData.ManagedOwner(p.m_ownerID));
        return held;
    }

    internal void Reattach(List<Minimap.PinData> held) => Pins(_map).AddRange(held);
    internal (int Changed, int Reused) Import(List<MapPin> incoming, string[]? keys)
    {
        var adopted = new HashSet<(string, int, Vector3)>();
        if (keys != null)
        {
            var ids = new HashSet<string>(keys);
            foreach (var old in _known.Values.Where(m => m.TableOnly && !ids.Contains(m.Id)).ToArray())
            {
                _known.Remove(old.Id);
                _dirty = true;
            }
        }

        if (keys != null)
            for (int i = 0; i < incoming.Count; i++)
            {
                var p = incoming[i];
                if (!_known.TryGetValue(keys[i], out var old))
                {
                    if (_known.Count < SharedMapPersonalMarkers.Maximum)
                    {
                        _known.Add(keys[i], new(keys[i], p) { TableOnly = true });
                        _dirty = true;
                    }

                    continue;
                }

                if (SharedMapTableData.CanonicalOwner(old.Pin.Owner) != SharedMapTableData.CanonicalOwner(p.Owner))
                    continue;
                if (old.PendingLocal) { old.PendingLocal = false; _dirty = true; }
                if (old.Adopted)
                    adopted.Add(Shape(p));
                if (Same(old.Pin, p))
                    continue;
                RemoveOverlay(old.Id);
                _known[old.Id] = new(old.Id, p)
                {
                    TableOnly = old.TableOnly,
                    Adopted = old.Adopted
                };
                _dirty = true;
            }

        var current = new Dictionary<(long, (string, int, Vector3)), MapPin>();
        foreach (var p in incoming)
            current[(p.Owner, Shape(p))] = p;
        int removed = 0;
        foreach (var p in Pins(_map).ToArray())
        {
            if (!p.m_save || !SharedMapTableData.ManagedOwner(p.m_ownerID))
                continue;
            var shape = Shape(p);
            if (!current.TryGetValue((p.m_ownerID, shape), out var value))
            {
                _map.RemovePin(p);
                removed++;
            }
            else
                p.m_checked = value.Checked;
        }

        var saved = Pins(_map).Where(p => p.m_save).ToLookup(Shape);
        var seen = new HashSet<(long, string, int, Vector3)>();
        int added = 0;
        foreach (var pin in incoming)
        {
            var key = Shape(pin);
            if (!seen.Add((pin.Owner, key.Item1, key.Item2, key.Item3)))
                continue;
            if (adopted.Contains(key) || saved[key].Any(p => p.m_ownerID == pin.Owner || p.m_ownerID == 0 || p.m_ownerID == _profile))
                continue;
            Add(pin, true);
            added++;
        }

        Draw();
        return (added + removed, seen.Count - added);
    }

    private void Draw()
    {
        var saved = new Dictionary<(string, int, Vector3), HashSet<long>>();
        foreach (var pin in Pins(_map).ToArray())
        {
            if (!pin.m_save)
                continue;
            var key = Shape(pin);
            if (!saved.TryGetValue(key, out var owners))
                saved.Add(key, owners = new());
            if (!owners.Add(pin.m_ownerID) && SharedMapTableData.ManagedOwner(pin.m_ownerID))
                _map.RemovePin(pin);
        }

        var portalPins = new Dictionary<(string, int, Vector3), Minimap.PinData>();
        var managedShapes = new HashSet<(string, int, Vector3)>(_known.Values.Select(m => Shape(m.Pin)));
        foreach (var pin in Pins(_map).ToArray())
        {
            if (!pin.m_save || !SharedMapTableData.ManagedOwner(pin.m_ownerID))
                continue;
            var key = Shape(pin);
            if (managedShapes.Contains(key) && saved.TryGetValue(key, out var owners) && (owners.Contains(0) || owners.Contains(_profile)))
                _map.RemovePin(pin);
            else if (SharedMapClientPortalStatus.IsPortal(pin))
                portalPins[key] = pin;
        }

        PortalStatus.BeginIndex();
        foreach (var pair in _known)
        {
            var p = pair.Value.Pin;
            saved.TryGetValue(Shape(p), out var owners);
            if (!pair.Value.Adopted && owners != null && (owners.Contains(0) || owners.Contains(_profile)))
            {
                pair.Value.Adopted = true;
                _dirty = true;
            }

            if ((pair.Value.PendingLocal && !_hasLocalPolicy) || pair.Value.TableOnly || pair.Value.Adopted || owners?.Contains(p.Owner) == true)
                RemoveOverlay(pair.Key);
            else if (!_overlay.ContainsKey(pair.Key))
                _overlay.Add(pair.Key, Add(p, false));

            if (SharedMapTableData.CanonicalOwner(p.Owner) != SharedMapTableData.PortalOwner || pair.Value.Adopted)
                continue;
            if (portalPins.TryGetValue(Shape(p), out var visible) || _overlay.TryGetValue(pair.Key, out visible))
                PortalStatus.Index(pair.Key, visible);
        }
        PortalStatus.EndIndex();
    }

    internal void Save(bool closing)
    {
        _file.Poll(closing);
        if (!_dirty || _file.Pending)
            return;
        byte[] data = SharedMapPersonalMarkers.Encode(_world, _profile, 0, Vector3.zero, 0, _known.Values.ToList());
        if (closing)
            _file.Write(data);
        else
            _file.Queue(data, 0);
        _dirty = false;
    }

    public void Dispose()
    {
        try
        {
            Save(true);
        }
        finally
        {
            PortalStatus.Clear();
            foreach (var key in _overlay.Keys.ToArray())
                RemoveOverlay(key);
        }
    }
}
