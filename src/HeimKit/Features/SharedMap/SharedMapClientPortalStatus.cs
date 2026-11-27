using System;
using System.Collections.Generic;
using HarmonyLib;
using HeimKit.Core.Native;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

// Render state only. Cartography and character saves continue to see the original PinData.
internal sealed class SharedMapClientPortalStatus
{
    private const string Suffix = " (Unconnected)";
    // Marker publication and checkmark slots can each move a pin by two metres per axis.
    private const float PublicationReach = 4.1f;
    private static readonly AccessTools.FieldRef<Minimap, bool> RefreshPins =
        NativeAccess.Field<Minimap, bool>("m_pinUpdateRequired");

    private sealed class Entry
    {
        internal readonly string Key;
        internal readonly Minimap.PinData Pin;
        internal bool Indexed;
        internal bool? Disconnected;
        internal string? Label;
        internal int Prefab;
        internal Vector3 Source;
        internal Entry(string key, Minimap.PinData pin) { Key = key; Pin = pin; }
    }

    private readonly Dictionary<Minimap.PinData, Entry> _entries = new();
    private readonly List<Entry> _order = new();
    private int _cursor;
    private float _next;
    private bool _enabled;

    internal static bool IsPortal(Minimap.PinData pin) => pin.m_type == Minimap.PinType.Icon4 &&
        SharedMapTableData.CanonicalOwner(pin.m_ownerID) == SharedMapTableData.PortalOwner;

    internal void BeginIndex()
    {
        foreach (var entry in _order) entry.Indexed = false;
    }

    internal void Index(string key, Minimap.PinData pin)
    {
        if (!IsPortal(pin) || pin.m_name.StartsWith("Multi Portals (", StringComparison.Ordinal)) return;
        if (!_entries.TryGetValue(pin, out var entry))
        {
            entry = new Entry(key, pin);
            _entries.Add(pin, entry);
            _order.Add(entry);
        }
        entry.Indexed = entry.Key == key;
    }

    internal void EndIndex()
    {
        _order.RemoveAll(entry =>
        {
            if (entry.Indexed) return false;
            Reset(entry);
            _entries.Remove(entry.Pin);
            return true;
        });
        if (_cursor >= _order.Count) _cursor = 0;
    }

    internal void Tick(float now, bool enabled, Dictionary<string, string> aliases, HashSet<string> groups,
        HashSet<string> exclusions, Action<string, string> trace)
    {
        if (!enabled)
        {
            if (_enabled) Clear();
            _enabled = false;
            return;
        }
        _enabled = true;
        if (now < _next || _order.Count == 0) return;
        _next = now + .25f;
        for (int n = 0; n < 4 && _cursor < _order.Count; n++)
        {
            var entry = _order[_cursor++];
            if (!IsPortal(entry.Pin) || aliases.ContainsKey(entry.Key) || groups.Contains(entry.Key) ||
                exclusions.Contains(entry.Key))
            {
                Reset(entry);
                continue;
            }
            var portal = Find(entry);
            if (portal == null) continue; // Unloaded or ambiguous carries no connection evidence.
            bool disconnected = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal).IsNone();
            if (entry.Disconnected == disconnected) continue;
            bool previouslyWhite = entry.Disconnected ?? (entry.Pin.m_ownerID == SharedMapTableData.UnconnectedPortalOwner);
            Reset(entry);
            entry.Disconnected = disconnected;
            if (disconnected != (entry.Pin.m_ownerID == SharedMapTableData.UnconnectedPortalOwner))
            {
                string label = entry.Pin.m_name;
                if (disconnected) label += Suffix;
                else if (label.EndsWith(Suffix, StringComparison.Ordinal)) label = label.Substring(0, label.Length - Suffix.Length);
                entry.Label = Localization.instance.Localize(label);
            }
            if (previouslyWhite == disconnected) continue;
            Redraw();
            trace("portal_status", $"id={entry.Key} portal={portal.m_uid} disconnected={disconnected} position={portal.GetPosition()}");
        }
        if (_cursor == _order.Count) { _cursor = 0; _next = now + 2; }
    }

    private static ZDO? Find(Entry entry)
    {
        var point = entry.Pin.m_pos;
        var offset = new Vector3(PublicationReach, 0, PublicationReach);
        var lo = ZoneSystem.GetZone(point - offset);
        var hi = ZoneSystem.GetZone(point + offset);
        ZDO? found = null;
        for (int x = lo.x; x <= hi.x; x++)
        for (int z = lo.y; z <= hi.y; z++)
        {
            var records = WorldObjectProvider.BorrowPortalSector(ZDOMan.instance, new Vector2s(x, z));
            if (records == null) continue;
            if (records.Count > 256) return null;
            for (int i = 0; i < records.Count; i++)
            {
                var portal = records[i];
                if (portal == null || !portal.IsValid() || portal.GetLong(ZDOVars.s_creator) == 0) continue;
                var source = portal.GetPosition();
                if (source.y != point.y || Mathf.Abs(source.x - point.x) > PublicationReach ||
                    Mathf.Abs(source.z - point.z) > PublicationReach) continue;
                int prefab = portal.GetPrefab();
                if (prefab != entry.Prefab || !source.Equals(entry.Source))
                {
                    string identity = SharedMapPortalIdentity.Create(prefab, source.x, source.y, source.z);
                    if (SharedMapPinState.MarkerKey(SharedMapTableData.PortalOwner, identity) != entry.Key) continue;
                    entry.Prefab = prefab;
                    entry.Source = source;
                }
                if (found != null && found.m_uid != portal.m_uid) return null;
                found = portal;
            }
        }
        return found;
    }

    internal bool? Display(Minimap.PinData pin)
    {
        if (!_enabled || !_entries.TryGetValue(pin, out var entry)) return null;
        var text = pin.m_NamePinData?.PinNameText;
        if (entry.Label != null && text != null && text.text != entry.Label) text.text = entry.Label;
        return entry.Disconnected;
    }

    private static void Redraw()
    {
        if (Minimap.instance != null) RefreshPins(Minimap.instance) = true;
    }

    private static void Reset(Entry entry)
    {
        if (entry.Label != null)
        {
            var text = entry.Pin.m_NamePinData?.PinNameText;
            if (text != null && text.text == entry.Label) text.text = Localization.instance.Localize(entry.Pin.m_name);
            Redraw();
        }
        entry.Label = null;
        entry.Disconnected = null;
    }

    internal void Clear()
    {
        foreach (var entry in _order) Reset(entry);
    }
}
