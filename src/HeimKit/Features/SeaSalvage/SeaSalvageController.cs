using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Features.SeaSalvage.Models;
using HeimKit.Shared;
using HeimKit.Shared.Players;
using UnityEngine;

namespace HeimKit.Features.SeaSalvage;
internal sealed class SeaSalvageController : IDisposable
{
    private static readonly int Consumed = "heimkit_seasalvage_consumed".GetStableHashCode();
    private readonly SeaSalvageSettings _settings;
    private readonly ManualLogSource _log;
    private readonly SeaSalvageDiagnostics _diagnostics;
    private readonly PlayerWorldScope _scope;
    private readonly ServerObjectAuthority _authority;
    private readonly WorldTerrainContext _terrain;
    private readonly Dictionary<ZDOID, SettlingObservation> _settling = new();
    private readonly Dictionary<int, ItemDrop?> _definitions = new();
    private readonly GameObject _crate;
    private readonly Container _container;
    private readonly ZDOMan _world = ZDOMan.instance;
    private float _next;
    private int _scanCursor;
    private long _transferred;
    public SeaSalvageController(SeaSalvage module, SeaSalvageSettings settings, ManualLogSource log, PlayerServices players, string directory)
    {
        _settings = settings; _log = log; _scope = new(players.Presence); _diagnostics = new(log, settings.DebugLogging);
        if (settings.MinimumDepthMeters.Value >= settings.MaximumDepthMeters.Value) throw new InvalidOperationException("SeaSalvage depth bounds must be ordered.");
        _crate = ZNetScene.instance.GetPrefab("CargoCrate"); _container = _crate?.GetComponent<Container>()!;
        if (_crate == null || _container == null || _container.m_width != 2 || _container.m_height != 2 ||
            _crate.GetComponent<Floating>() == null || _crate.GetComponent<ZNetView>()?.m_persistent != true ||
            _container.m_defaultItems.m_drops.Count != 0) throw new InvalidOperationException("Native floating four-slot CargoCrate is unavailable.");
        _terrain = new();
        try { _authority = new(module.Fail, log, players.Presence, false); } catch { _terrain.Dispose(); throw; }
        try { _scope.Index(z => Definition(z.GetPrefab()) != null); } catch { Dispose(); throw; }
    }
    private ItemDrop? Definition(int hash)
    {
        if (_definitions.TryGetValue(hash, out var definition)) return definition;
        var prefab = ZNetScene.instance.GetPrefab(hash); definition = prefab?.GetComponent<ItemDrop>();
        if (prefab == null || prefab.GetComponent<ZNetView>()?.m_persistent != true ||
            prefab.GetComponent<Floating>() != null || prefab.GetComponent<Fish>() != null || prefab.GetComponent<Character>() != null ||
            prefab.GetComponent<Piece>() != null || prefab.GetComponent<Container>() != null || prefab.GetComponent<TombStone>() != null) definition = null;
        if (_definitions.Count < 4096) _definitions[hash] = definition;
        return definition;
    }
    public void Tick(float now)
    {
        if (now < _next) return; _next = now + _settings.IntervalSeconds.Value;
        using var timing = _settings.DebugLogging.Enabled ? _diagnostics.Measure(now, () => _scope.Records.Count) : null;
        // Do not release a consumed record before native destruction removes it.
        foreach (var id in _held.ToArray())
            if (_world.GetZDO(id) == null) { _authority.Release(id); _held.Remove(id); }
        if (!_scope.Refresh(_settings.ScanRangeMeters.Value)) { _settling.Clear(); _terrain.Clear(); return; }
        foreach (var id in _settling.Where(p => now - p.Value.Seen > 120 || now < p.Value.Since || _world.GetZDO(p.Key) == null).Select(p => p.Key).ToArray()) _settling.Remove(id);
        var ready = new List<SalvageSource>(); int probes = 0;
        int candidates = 0, owners = 0, players = 0, settling = 0, terrain = 0, depths = 0, invalid = 0;
        int start = _scope.Records.Count == 0 ? 0 : _scanCursor % _scope.Records.Count;
        for (int visited = 0; visited < _scope.Records.Count; visited++)
        {
            int index = (start + visited) % _scope.Records.Count;
            var source = _scope.Records[index];
            _scanCursor = (index + 1) % _scope.Records.Count;
            if (ready.Count >= _settings.MaxItemsPerPass.Value || _held.Count >= 256) break;
            if (!source.IsValid() || source.GetBool(Consumed) || source.GetBool(ZDOVars.s_piece) || !_scope.InRange(source.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            var definition = Definition(source.GetPrefab()); if (definition == null) continue;
            candidates++;
            var point = source.GetPosition(); long owner = source.GetOwner();
            if (owner != 0 && owner != ZDOMan.GetSessionID()) { owners++; _settling.Remove(source.m_uid); continue; }
            if (Character.InInterior(point) || _scope.PlayerNear(point, 10)) { players++; _settling.Remove(source.m_uid); continue; }
            // Cheap exclusion before preparing any terrain: vanilla outdoor water baseline.
            if (point.y > ZoneSystem.c_WaterLevel - _settings.MinimumDepthMeters.Value) { depths++; continue; }
            byte[] bytes = source.GetByteArray(ZDOVars.s_itemData);
            if (bytes == null || bytes.Length < 10 || bytes.Length > 262144 || bytes[0] != 110) { invalid++; continue; }
            if (!_settling.TryGetValue(source.m_uid, out var observed) || observed.Owner != owner || observed.Revision != source.DataRevision || (observed.Position - point).sqrMagnitude > 0.01f)
            {
                if (_settling.Count >= 4096 && !_settling.ContainsKey(source.m_uid)) continue;
                _settling[source.m_uid] = new SettlingObservation { Owner = owner, Revision = source.DataRevision, Position = point, Since = now, Seen = now }; continue;
            }
            observed.Seen = now;
            if (now - observed.Since < _settings.DelaySeconds.Value) { settling++; continue; }
            if (++probes > 4) { _scanCursor = index; break; }
            if (!_scope.ContextReady() || !_terrain.Prepare(point, _scope.Context, now)) { terrain++; continue; }
            var map = Heightmap.FindHeightmap(point);
            if (map == null || map.IsLava(point)) continue;
            float water = Floating.GetLiquidLevel(point, 0, LiquidType.Water), depth = water - point.y;
            if (!PlayerWorldScope.Finite(water) || water <= -9999f || depth < _settings.MinimumDepthMeters.Value || depth > _settings.MaximumDepthMeters.Value) { depths++; continue; }
            var item = definition.m_itemData.Clone();
            var itemPacket = new ZPackage(bytes); var format = (global::Version.Item)itemPacket.ReadByte();
            int itemPrefab;
            try { itemPrefab = ItemDrop.ItemData.Load(itemPacket, item, format); }
            catch (System.IO.IOException) { invalid++; continue; }
            catch (ArgumentException) { invalid++; continue; }
            if (itemPrefab != source.GetPrefab() || itemPacket.GetPos() != itemPacket.Size() || (bytes[8] & 64) == 0) continue;
            item.m_dropPrefab = definition.gameObject;
            if (item.m_stack <= 0 || item.m_stack > item.m_shared.m_maxStackSize || item.m_quality < 1 || !PlayerWorldScope.Finite(item.m_durability)) continue;
            var empty = item.Clone(); empty.m_stack = 0; var emptyData = new ZPackage(); emptyData.Write((byte)110); empty.Save(emptyData);
            ready.Add(new SalvageSource { Record = source, Identity = source.m_uid, Position = point, Revision = source.DataRevision, Owner = owner,
                Original = (byte[])bytes.Clone(), Empty = emptyData.GetArray() });
        }
        int count = 0;
        while (ready.Count > 0)
        {
            var first = ready[0]; ready.RemoveAt(0); var group = new List<SalvageSource> { first };
            if (_settings.GroupRadiusMeters.Value > 0)
                for (int i = 0; i < ready.Count && group.Count < _settings.SlotsPerCrate.Value;)
                    if (Utils.DistanceXZ(first.Position, ready[i].Position) <= _settings.GroupRadiusMeters.Value) { group.Add(ready[i]); ready.RemoveAt(i); } else i++;
            count += Transfer(group);
        }
        _transferred += count;
        _diagnostics.Scan(now, _scope.Records.Count, candidates, owners, players, settling, terrain, depths, invalid);
        if (count > 0) DiagnosticLog.Write(_log, "SeaSalvage", DiagnosticEvent.Result, count == 1 ? "Transferred one item drop into floating cargo." : $"Transferred {count} item drops into floating cargo.");
        _diagnostics.Result(now, count, _transferred, _settling.Count);
    }
    private readonly HashSet<ZDOID> _held = new();
    private int Transfer(List<SalvageSource> group)
    {
        foreach (var source in group)
            if (!source.Record.IsValid() || source.Record.m_uid != source.Identity || source.Record.DataRevision != source.Revision || source.Record.GetOwner() != source.Owner ||
                source.Record.GetBool(Consumed) || !source.Original.SequenceEqual(source.Record.GetByteArray(ZDOVars.s_itemData) ?? Array.Empty<byte>()) || _scope.PlayerNear(source.Position, 10)) return 0;
        var point = group[0].Position;
        float water = Floating.GetLiquidLevel(point, 0, LiquidType.Water);
        if (!PlayerWorldScope.Finite(water) || water <= -9999f || water < point.y + _settings.MinimumDepthMeters.Value) return 0;
        point.y = water + 0.5f;
        byte[] contents = SeaSalvageInventory.Pack(group);
        var emptyInventory = new Inventory(_container.m_name, _container.m_bkg, 2, 2); var emptyContents = new ZPackage(); emptyInventory.Save(emptyContents);
        ZDO? cargo = null; bool committed = false;
        try
        {
            foreach (var source in group) { _authority.Retain(source.Record); _held.Add(source.Identity); }
            cargo = NativeWorldObject.Create(_crate, point, Quaternion.identity);
            cargo.Set(ZDOVars.s_addedDefaultItems, true); cargo.Set(ZDOVars.s_items, contents);
            foreach (var source in group)
            { source.Record.Set(ZDOVars.s_itemData, source.Empty); source.Record.Set(Consumed, true); }
            committed = true; // Inventory and consumed source state form one main-thread world snapshot.
            foreach (var source in group) _diagnostics.Transfer(Time.unscaledTime, source.Identity, cargo.m_uid);
            cargo.SetOwner(0); _world.ForceSendZDO(cargo.m_uid);
            foreach (var source in group)
            {
                var instance = ZNetScene.instance.FindInstance(source.Record);
                if (instance != null) ZNetScene.instance.Destroy(instance.gameObject); else _world.DestroyZDO(source.Record);
                _settling.Remove(source.Identity);
            }
            return group.Count;
        }
        catch
        {
            if (!committed)
            {
                // An unpublished crate is emptied before rollback; it cannot persist duplicate inventory.
                if (cargo != null && cargo.IsValid()) { cargo.Set(ZDOVars.s_items, emptyContents.GetArray()); _world.DestroyZDO(cargo); }
                foreach (var source in group)
                {
                    if (source.Record.IsValid() && source.Record.m_uid == source.Identity)
                    { source.Record.Set(ZDOVars.s_itemData, source.Original); source.Record.Set(Consumed, false); }
                    _authority.Release(source.Identity); _held.Remove(source.Identity);
                }
            }
            throw;
        }
    }
    public void Dispose() { try { _authority.Dispose(); } finally { try { _scope.Dispose(); } finally { _terrain.Dispose(); _settling.Clear(); _held.Clear(); } } }
}
