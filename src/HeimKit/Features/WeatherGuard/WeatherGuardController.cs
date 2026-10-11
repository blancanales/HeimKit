using HeimKit.Features.WeatherGuard.Models;
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace HeimKit.Features.WeatherGuard;

// The server runs vanilla combat/support for protected pieces. Clients keep ordinary RPCs.
internal sealed class WeatherGuardController : IDisposable
{
    private readonly WeatherGuard _module;
    private readonly ManualLogSource _log;
    private readonly WeatherGuardDiagnostics _diagnostics;
    private readonly ZDOMan _manager;
    private readonly WeatherGuardPatches _patches;
    private readonly List<ZDO> _near = new(), _distant = new(), _scratch = new(), _farScratch = new();
    private readonly HashSet<ZDOID> _nearIds = new(), _distantIds = new();
    private readonly HashSet<ZDOID> _instanceSeen = new();
    private readonly HashSet<Vector2s> _regions = new();
    private readonly List<Vector3> _positions = new();
    private readonly Dictionary<ZDOID, ZDO> _protected = new();
    private readonly Dictionary<Vector2s, bool> _readyContexts = new();
    private readonly List<ZDOID> _release = new();
    private readonly MethodInfo _createZones = typeof(ZoneSystem).GetMethod("CreateLocalZones", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly FieldInfo _instancesField = AccessTools.Field(typeof(ZNetScene), "m_instances");
    private float _nextRefresh, _nextTerrain;
    private bool _limitReported;
    private bool _announced;
    private int _eligible, _waiting, _waitingTerrain, _waitingObjects, _waitingInstance;
    private long _acquired, _released, _blockedData, _blockedOwner, _blockedBroadcast, _commands, _wearPasses;
    internal static WeatherGuardController? Active { get; private set; }
    internal long ServerId => ZDOMan.GetSessionID();
    internal bool Running => Active == this && _module.IsActive && ReferenceEquals(_manager, ZDOMan.instance) &&
        ZNetScene.instance != null && ZNet.instance != null && ZNet.instance.IsDedicated() && ZNet.instance.IsServer();

    public WeatherGuardController(WeatherGuard module, ManualLogSource log, bool debug)
    { _module = module; _log = log; _diagnostics = new WeatherGuardDiagnostics(log, debug); _manager = ZDOMan.instance; _patches = new WeatherGuardPatches(); }
    public void Install()
    {
        if (_createZones == null || _instancesField == null) throw new MissingMemberException("WeatherGuard terrain/instance APIs unavailable.");
        Active = this;
        try { _patches.Install(); } catch { Dispose(); throw; }
    }
    internal bool Protects(ZDOID id) => Running && _protected.ContainsKey(id);
    internal void Fail(Exception error) => _module.Fail(error);
    internal void OwnerBlocked() => _blockedOwner++;
    internal void DataBlocked(int count) => _blockedData += count;
    internal void Reassert(ZDOID id, uint revision, ushort ownerRevision)
    {
        if (!_protected.TryGetValue(id, out var zdo) || !zdo.IsValid()) return;
        var next = WeatherGuardRules.ReassertRevisions(zdo.DataRevision, zdo.OwnerRevision, revision, ownerRevision);
        // Advance metadata, not client health/support/creator. A client with a newer local
        // revision must accept the server's authoritative snapshot on its next receive.
        zdo.DataRevision = next.Data; zdo.OwnerRevision = next.Owner;
        ZDOMan.instance.ForceSendZDO(id);
    }
    internal void BroadcastBlocked(int count = 1) => _blockedBroadcast += count;
    internal void Resend(ZDOID id)
    {
        if (_protected.TryGetValue(id, out var zdo) && zdo.IsValid()) ZDOMan.instance.ForceSendZDO(id);
    }
    internal void CommandRouted() => _commands++;
    internal void WearPass() => _wearPasses++;

    public void Tick(float now)
    {
        if (now < _nextRefresh) return;
        _nextRefresh = now + 1f;
        var manager = ZDOMan.instance; var scene = ZNetScene.instance; var zones = ZoneSystem.instance;
        if (manager == null || scene == null || zones == null) return;
        _near.Clear(); _distant.Clear(); _nearIds.Clear(); _distantIds.Clear(); _regions.Clear(); _positions.Clear(); _readyContexts.Clear();
        bool limited = false;
        foreach (var peer in ZNet.instance.GetPeers())
        {
            if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
            var character = manager.GetZDO(peer.m_characterID);
            if (character == null || !character.IsValid() || character.GetOwner() != peer.m_uid) continue;
            var position = character.GetPosition();
            var sector = ZoneSystem.GetZone(position);
            if (_regions.Contains(sector)) continue;
            if (_regions.Count >= WeatherGuardRules.MaximumRegions) { limited = true; continue; }
            _scratch.Clear(); _farScratch.Clear();
            // Use the server's distance, matching terrain/instance readiness checks.
            manager.FindSectorObjects(sector, ZNet.instance.GetSyncedSimulationDistance(), _scratch, _farScratch);
            if (_near.Count + _distant.Count + _scratch.Count + _farScratch.Count > WeatherGuardRules.MaximumObjects)
            { limited = true; continue; }
            _regions.Add(sector); _positions.Add(position);
            foreach (var zdo in _scratch) if (_nearIds.Add(zdo.m_uid)) _near.Add(zdo);
            foreach (var zdo in _farScratch) if (_distantIds.Add(zdo.m_uid)) _distant.Add(zdo);
        }
        // A later region may need a full instance of an earlier region's distant object.
        _distant.RemoveAll(zdo => _nearIds.Contains(zdo.m_uid));
        // Release authority before vanilla removes instances outside retained regions.
        _release.Clear();
        foreach (var pair in _protected)
        {
            var zdo = manager.GetZDO(pair.Key);
            if (zdo == null || !zdo.IsValid() || !_nearIds.Contains(pair.Key) || !IsEligible(zdo, scene) || !Ready(zdo, scene, zones))
                _release.Add(pair.Key);
        }
        foreach (var id in _release) Release(id);
        _eligible = _waiting = _waitingTerrain = _waitingObjects = _waitingInstance = 0;
        foreach (var zdo in _near)
        {
            if (!IsEligible(zdo, scene)) continue;
            _eligible++;
            if (_protected.ContainsKey(zdo.m_uid)) continue;
            if (_protected.Count >= WeatherGuardRules.MaximumProtected) { limited = true; break; }
            bool instanceReady = scene.FindInstance(zdo) != null;
            bool terrainReady = TerrainReady(zdo, zones);
            bool objectsReady = terrainReady && ContextReady(zdo, scene, zones);
            if (!WeatherGuardRules.MayAcquire(instanceReady, terrainReady, objectsReady, true))
            { _waiting++; if (!terrainReady) _waitingTerrain++; if (!instanceReady) _waitingInstance++; if (terrainReady && !objectsReady) _waitingObjects++; continue; }
            // Insert before changing owner so ownership guards cover the whole transition.
            _protected.Add(zdo.m_uid, zdo);
            // A server instance may precede the client's creator-data update. Refresh only
            // the server's cached attribution; never assign or transfer persistent creators.
            var piece = scene.FindInstance(zdo).GetComponent<Piece>();
            AccessTools.Field(typeof(Piece), "m_creator").SetValue(piece, zdo.GetLong(ZDOVars.s_creator));
            AccessTools.Field(typeof(Piece), "m_creatorPlatformUserIDIndex").SetValue(piece, zdo.GetInt(ZDOVars.s_creatorIndex, -1));
            zdo.SetOwner(ServerId);
            manager.ForceSendZDO(zdo.m_uid);
            _acquired++;
            _diagnostics.Sample(now, () => $"server control acquired; object={zdo.m_uid}; prefab={scene.GetPrefab(zdo.GetPrefab()).name}; creator preserved=true; health={zdo.GetFloat(ZDOVars.s_health, -1f)}; owner={ServerId}");
        }
        if (_protected.Count > 0 && !_announced)
        {
            _announced = true;
            _log.LogInfo($"WeatherGuard: server control active for {_protected.Count} player-built structure piece(s). Existing creators preserved; no player reload required. In-game rain/combat/support checks still required.");
        }
        if (limited && !_limitReported)
            _log.LogWarning("WeatherGuard: processing capacity reached. Unmanaged pieces remain under vanilla processing; see debug status. Protection is not complete for those pieces.");
        _limitReported = limited;
        _diagnostics.Report(now, () => new WeatherGuardStatus
        {
            Protected = _protected.Count, Eligible = _eligible, Waiting = _waiting,
            WaitingTerrain = _waitingTerrain, WaitingInstance = _waitingInstance, WaitingObjects = _waitingObjects,
            Regions = _regions.Count, Nearby = _near.Count, Distant = _distant.Count,
            Acquired = _acquired, Released = _released, BlockedData = _blockedData, BlockedOwner = _blockedOwner,
            BlockedBroadcast = _blockedBroadcast, Commands = _commands, WearPasses = _wearPasses, Limited = limited
        });
    }
    private static bool IsEligible(ZDO zdo, ZNetScene scene)
    {
        if (!zdo.IsValid()) return false;
        var prefab = scene.GetPrefab(zdo.GetPrefab());
        // Container inventory changes require client ownership and must not be filtered.
        return prefab != null && prefab.TryGetComponent<Piece>(out var piece) && prefab.TryGetComponent<WearNTear>(out var wear) &&
            WeatherGuardRules.Eligible(zdo.GetLong(ZDOVars.s_creator), zdo.Persistent, wear.m_staticPosition,
                wear.m_noRoofWear, zdo.GetFloat(ZDOVars.s_health, wear.m_health),
                piece.m_category == Piece.PieceCategory.BuildingWorkbench || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth,
                prefab.GetComponent<Container>() != null);
    }
    private static bool TerrainReady(ZDO zdo, ZoneSystem zones)
    {
        var sector = zdo.GetSector();
        // Support can cross sector edges: do not take ownership with only the central terrain loaded.
        for (short x = (short)(sector.x - 1); x <= sector.x + 1; x++)
        for (short y = (short)(sector.y - 1); y <= sector.y + 1; y++)
            if (!zones.IsZoneLoaded(new Vector2s(x, y))) return false;
        return true;
    }
    private bool ContextReady(ZDO zdo, ZNetScene scene, ZoneSystem zones)
    {
        var sector = zdo.GetSector();
        if (_readyContexts.TryGetValue(sector, out bool ready)) return ready;
        ready = TerrainReady(zdo, zones) && scene.IsAreaReady(zdo.GetPosition());
        _readyContexts.Add(sector, ready);
        return ready;
    }
    private bool Ready(ZDO zdo, ZNetScene scene, ZoneSystem zones)
        => scene.FindInstance(zdo) != null && ContextReady(zdo, scene, zones);
    internal void LoadTerrain()
    {
        if (!Running || ZoneSystem.instance == null || !ZoneSystem.instance.LocationsGenerated) return;
        float now = Time.unscaledTime;
        if (now < _nextTerrain) return;
        _nextTerrain = now + 0.1f;
        foreach (var position in _positions) _createZones.Invoke(ZoneSystem.instance, new object[] { position });
    }
    internal void AddInstances(List<ZDO> near, List<ZDO> distant)
    {
        if (!Running) return;
        var seen = _instanceSeen; seen.Clear();
        foreach (var zdo in near) seen.Add(zdo.m_uid);
        foreach (var zdo in distant) seen.Add(zdo.m_uid);
        foreach (var zdo in _near) if (zdo.IsValid() && seen.Add(zdo.m_uid)) near.Add(zdo);
        foreach (var zdo in _distant) if (zdo.IsValid() && seen.Add(zdo.m_uid)) distant.Add(zdo);
        // Vanilla may create drops/fragments/new objects between our one-second scans.
        // Keep those live instances until the next scan; otherwise RemoveObjects could
        // delete server-owned nonpersistent drops outside the server reference area.
        var instances = (Dictionary<ZDO, ZNetView>)_instancesField.GetValue(ZNetScene.instance);
        foreach (var pair in instances)
        {
            var zdo = pair.Key;
            if (!zdo.IsValid() || seen.Contains(zdo.m_uid)) continue;
            foreach (var position in _positions)
                if (ZNetScene.InActiveArea(zdo.GetPosition(), position))
                { seen.Add(zdo.m_uid); near.Add(zdo); break; }
        }
    }
    private void Release(ZDOID id)
    {
        if (!_protected.TryGetValue(id, out var zdo)) return;
        _protected.Remove(id);
        if (zdo.IsValid() && zdo.GetOwner() == ServerId) zdo.SetOwner(0L);
        _released++;
    }
    public void Dispose()
    {
        _diagnostics.Reset();
        if (Active == this) Active = null;
        try { _patches.Dispose(); }
        finally
        {
            // With guards inactive, let vanilla choose a new owner even if unpatching failed.
            try
            {
                foreach (var zdo in _protected.Values)
                    if (zdo.IsValid() && ZDOMan.instance != null && zdo.GetOwner() == ZDOMan.GetSessionID()) zdo.SetOwner(0L);
            }
            finally { _protected.Clear(); _positions.Clear(); _near.Clear(); _distant.Clear(); }
        }
    }
}
