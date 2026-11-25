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
using BepInEx.Logging;
using HeimKit.Features.SeaSalvage.Models;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.SeaSalvage;
internal sealed class SeaSalvageController : IDisposable
{
    private static readonly int Consumed = "heimkit_seasalvage_consumed".GetStableHashCode();
    private readonly SeaSalvageSettings _settings;
    private readonly ManualLogSource _log;
    private readonly SeaSalvageDiagnostics _diagnostics;
    private readonly PlayerWorldScope _scope;
    private readonly ObjectAuthority _authority;
    private readonly TerrainContext _terrain;
    private readonly Dictionary<ZDOID, SettlingObservation> _settling = new();
    private readonly PrefabCatalog<ItemDrop?> _definitions = PrefabRegistry.Current.Catalog<ItemDrop?>("SeaSalvageController._definitions");
    private readonly GameObject _crate;
    private readonly Container _container;
    private readonly ZDOMan _world = ZDOMan.instance;
    private int _scanCursor;
    private readonly AdaptiveOperation _discovery, _evaluation, _cargo;
    private readonly List<ZDOID> _ids = new();
    private readonly List<ZDOID> _settlingKeys = new();
    private readonly HashSet<ZDOID> _visited = new();
    private IEnumerator<bool>? _scan, _pass;
    private float _now;
    private long _transferred;
    private int _passTransferred;
    private readonly List<ZDOID> _remove = new();
    private readonly List<SalvageSource> _ready = new(), _group = new();
    public SeaSalvageController(SeaSalvage module, SeaSalvageSettings settings, ManualLogSource log, PlayerServices players, string directory)
    {
        _settings = settings; _log = log; _scope = WorldObjectProvider.Current.CreateScope("SeaSalvage"); _diagnostics = new(log, settings.DebugLogging);
        _discovery = new(log, settings.DebugLogging, "SeaSalvage", "discovery_cells", 32);
        _evaluation = new(log, settings.DebugLogging, "SeaSalvage", "drop_evaluation", 128);
        _cargo = new(log, settings.DebugLogging, "SeaSalvage", "crate_transfer");
        if (settings.MinimumDepthMeters.Value >= settings.MaximumDepthMeters.Value) throw new InvalidOperationException("SeaSalvage depth bounds must be ordered.");
        _crate = PrefabRegistry.Current.Prefab("CargoCrate")!; _container = PrefabRegistry.Component<Container>(_crate)!;
        if (_crate == null || _container == null || _container.m_width != 2 || _container.m_height != 2 ||
            PrefabRegistry.Component<Floating>(_crate) == null || PrefabRegistry.Component<ZNetView>(_crate)?.m_persistent != true ||
            _container.m_defaultItems.m_drops.Count != 0) throw new InvalidOperationException("Native floating four-slot CargoCrate is unavailable.");
        _terrain = new();
        try { _authority = new(module.Fail, log, players.Presence, false); } catch { _terrain.Dispose(); throw; }
        try { _scope.Index(z => Definition(z.GetPrefab()) != null); } catch { Dispose(); throw; }
    }
    private ItemDrop? Definition(int hash)
    {
        if (_definitions.TryGetValue(hash, out var definition)) return definition;
        var prefab = PrefabRegistry.Current.Prefab(hash); definition = PrefabRegistry.Component<ItemDrop>(prefab);
        if (prefab == null || PrefabRegistry.Component<ZNetView>(prefab)?.m_persistent != true ||
            PrefabRegistry.Component<Floating>(prefab) != null || PrefabRegistry.Component<Fish>(prefab) != null || PrefabRegistry.Component<Character>(prefab) != null ||
            PrefabRegistry.Component<Piece>(prefab) != null || PrefabRegistry.Component<Container>(prefab) != null || PrefabRegistry.Component<TombStone>(prefab) != null) definition = null;
        if (_definitions.Count < 4096) _definitions[hash] = definition;
        return definition;
    }
    internal bool HoldNewWork { get; set; }
    internal bool Idle => _held.Count == 0;

    public void Tick(float now)
    {
        // Do not release a consumed record before native destruction removes it.
        _remove.Clear(); foreach (var id in _held) if (_world.GetZDO(id) == null) _remove.Add(id);
        foreach (var id in _remove) { _authority.Release(id); _held.Remove(id); }
        _now = now;
        if (HoldNewWork || !WorldObjectProvider.Current.Ready) { CancelPass(); return; }
        if (_ready.Count > 0 && _pass == null)
        {
            if (_cargo.Due(this, now, 0)) TransferNext(now);
            return;
        }
        if (_scan == null && _pass == null)
        {
            if (!ModuleScheduler.Current.Due(this, "processing", now, _settings.IntervalSeconds.Value, cooperative: true)) return;
            _ids.Clear(); _visited.Clear();
            if (!_scope.BeginCandidateScan(_settings.ScanRangeMeters.Value, _ids, out _scan, outdoorOnly: false, includeDistant: true))
            { _settling.Clear(); _terrain.Clear(); return; }
        }
        if (_scan != null)
        {
            if (!_discovery.Due(this, now, 0)) return;
            using var batch = _discovery.Begin(now);
            bool complete = WorkSlice.Advance(_scan, adaptive: batch);
            if (_ids.Count > 8192) { CancelPass(); _settling.Clear(); _terrain.Clear(); return; }
            if (!complete) return;
            _scan.Dispose(); _scan = null;
            _pass = Evaluate().GetEnumerator();
            return;
        }
        if (!_evaluation.Due(this, now, 0)) return;
        if (!_scope.ReadActors()) { CancelPass(); _settling.Clear(); _terrain.Clear(); return; }
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _ids.Count) : null;
        using var evaluation = _evaluation.Begin(now);
        if (WorkSlice.Advance(_pass!, adaptive: evaluation))
        {
            _pass!.Dispose(); _pass = null;
            ModuleScheduler.Current.Postpone(this, "processing", now, _settings.IntervalSeconds.Value);
        }
    }

    private IEnumerable<bool> Evaluate()
    {
        float now = _now;
        _settlingKeys.Clear(); _settlingKeys.AddRange(_settling.Keys);
        for (int index = 0; index < _settlingKeys.Count; index++)
        {
            if (index % 16 == 0) yield return true;
            var id = _settlingKeys[index];
            var observation = _settling[id];
            if (_now - observation.Seen > 120 || _now < observation.Since || _world.GetZDO(id) == null)
                _settling.Remove(id);
        }
        var ready = _ready; ready.Clear(); int probes = 0;
        int candidates = 0, owners = 0, players = 0, settling = 0, terrain = 0, depths = 0, invalid = 0;
        int start = _ids.Count == 0 ? 0 : _scanCursor % _ids.Count;
        for (int visited = 0; visited < _ids.Count; visited++)
        {
            yield return true;
            now = _now;
            int index = (start + visited) % _ids.Count;
            var id = _ids[index];
            _scanCursor = (index + 1) % _ids.Count;
            if (!_visited.Add(id)) continue;
            var source = _world.GetZDO(id);
            if (source == null || source.m_uid != id) continue;
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
            using (PerformanceStages.Measure("SeaSalvage", "depth_context", 1))
                if (!_scope.ContextReady() || !_terrain.Prepare(point, _scope.Context, now)) { terrain++; continue; }
            var map = Heightmap.FindHeightmap(point);
            if (map == null || map.IsLava(point)) continue;
            float water = Floating.GetLiquidLevel(point, 0, LiquidType.Water), depth = water - point.y;
            if (!PlayerWorldScope.Finite(water) || water <= -9999f || depth < _settings.MinimumDepthMeters.Value || depth > _settings.MaximumDepthMeters.Value) { depths++; continue; }
            using var decode = PerformanceStages.Measure("SeaSalvage", "item_decode", 1, bytes.Length);
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
        _diagnostics.Scan(now, _ids.Count, candidates, owners, players, settling, terrain, depths, invalid);
        if (ready.Count == 0) _diagnostics.Result(now, 0, _transferred, _settling.Count);
    }

    private void TransferNext(float now)
    {
        using var batch = _cargo.Begin(now);
        if (!_scope.ReadActors()) { CancelPass(); return; }
        var first = _ready[0]; _ready.RemoveAt(0);
        _group.Clear(); _group.Add(first);
        if (_settings.GroupRadiusMeters.Value > 0)
            for (int i = 0; i < _ready.Count && _group.Count < _settings.SlotsPerCrate.Value;)
                if (Utils.DistanceXZ(first.Position, _ready[i].Position) <= _settings.GroupRadiusMeters.Value)
                { _group.Add(_ready[i]); _ready.RemoveAt(i); }
                else i++;
        int bytes = 0;
        foreach (var source in _group) bytes += source.Original.Length;
        batch.Count(bytes: bytes);
        int count = Transfer(_group);
        _transferred += count;
        _passTransferred += count;
        if (_ready.Count == 0) ReportTransfers(now);
    }

    private void ReportTransfers(float now)
    {
        if (_passTransferred > 0)
            DiagnosticLog.Write(_log, "SeaSalvage", _passTransferred == 1 ? "Transferred one item drop into floating cargo." : $"Transferred {_passTransferred} item drops into floating cargo.");
        _diagnostics.Result(now, _passTransferred, _transferred, _settling.Count);
        _passTransferred = 0;
    }

    private void CancelPass()
    {
        if (_passTransferred > 0) ReportTransfers(_now);
        _scan?.Dispose(); _scan = null;
        _pass?.Dispose(); _pass = null;
        _ready.Clear(); _group.Clear(); _ids.Clear(); _visited.Clear();
    }
    private readonly HashSet<ZDOID> _held = new();
    private int Transfer(List<SalvageSource> group)
    {
        if (_held.Count + group.Count > 256) return 0;
        foreach (var source in group)
        {
            if (!ReferenceEquals(_world.GetZDO(source.Identity), source.Record) || !source.Record.IsValid() ||
                source.Record.m_uid != source.Identity) return 0;
            var position = source.Record.GetPosition();
            if (!_settling.TryGetValue(source.Identity, out var settled) || _now - settled.Since < _settings.DelaySeconds.Value ||
                source.Record.DataRevision != source.Revision || source.Record.GetOwner() != source.Owner ||
                source.Record.GetBool(Consumed) || !source.Original.SequenceEqual(source.Record.GetByteArray(ZDOVars.s_itemData) ?? Array.Empty<byte>()) || (position - source.Position).sqrMagnitude > .01f ||
                !_scope.Positions.Any(player => (player - position).sqrMagnitude <= _settings.ScanRangeMeters.Value * _settings.ScanRangeMeters.Value) ||
                _scope.PlayerNear(position, 10) || Character.InInterior(position)) return 0;
            var map = Heightmap.FindHeightmap(position);
            float level = Floating.GetLiquidLevel(position, 0, LiquidType.Water);
            if (map == null || map.IsLava(position) || !PlayerWorldScope.Finite(level) || level <= -9999f ||
                level - position.y < _settings.MinimumDepthMeters.Value || level - position.y > _settings.MaximumDepthMeters.Value) return 0;
        }
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
            ObjectAuthority.Assign(cargo, 0); _world.ForceSendZDO(cargo.m_uid);
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
    public void Dispose() { CancelPass(); try { _authority.Dispose(); } finally { try { _scope.Dispose(); } finally { _terrain.Dispose(); _settling.Clear(); _held.Clear(); } } }
}
