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
    private readonly Dictionary<string, int> _exclusions = new();
    private readonly Dictionary<string, int> _releaseReasons = new();
    private readonly FieldInfo _roof = AccessTools.Field(typeof(WearNTear), "m_haveRoof");
    private readonly FieldInfo _shield = AccessTools.Field(typeof(WearNTear), "m_shieldChangeID");
    private readonly FieldInfo _rainTimer = AccessTools.Field(typeof(WearNTear), "m_rainTimer");
    private readonly MethodInfo _shouldUpdate = AccessTools.Method(typeof(WearNTear), "ShouldUpdate");
    private readonly MethodInfo _underWater = AccessTools.Method(typeof(WearNTear), "IsUnderWater");
    private readonly MethodInfo _canBeRemoved = AccessTools.Method(typeof(WearNTear), "CanBeRemoved");
    private readonly MethodInfo _createZones = typeof(ZoneSystem).GetMethod("CreateLocalZones", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly FieldInfo _instancesField = AccessTools.Field(typeof(ZNetScene), "m_instances");
    private float _nextRefresh, _nextTerrain;
    private bool _limitReported;
    private bool _announced;
    private int _eligible, _waiting, _waitingTerrain, _waitingObjects, _waitingInstance;
    private long _acquired, _released, _blockedData, _blockedOwner, _blockedBroadcast, _commands, _wearPasses;
    private long _ownerLosses, _weatherGates, _wetOpportunities;
    private long _nativeCommands, _supportPasses;
    private int _peers, _readyPeers, _characterWaits;
    internal static WeatherGuardController? Active { get; private set; }
    internal long ServerId => ZDOMan.GetSessionID();
    internal bool Running => Active == this && _module.IsActive && ReferenceEquals(_manager, ZDOMan.instance) &&
        ZNetScene.instance != null && ZNet.instance != null && ZNet.instance.IsDedicated() && ZNet.instance.IsServer();

    public WeatherGuardController(WeatherGuard module, ManualLogSource log, bool debug)
    { _module = module; _log = log; _diagnostics = new WeatherGuardDiagnostics(log, debug); _manager = ZDOMan.instance; _patches = new WeatherGuardPatches(); }
    public void Install()
    {
        if (_createZones == null || _instancesField == null || _roof == null || _shield == null || _rainTimer == null ||
            _shouldUpdate == null || _underWater == null || _canBeRemoved == null) throw new MissingMemberException("WeatherGuard terrain/instance/wear evidence APIs unavailable.");
        Active = this;
        try { _patches.Install(); } catch { Dispose(); throw; }
    }
    internal bool Protects(ZDOID id) => Running && _protected.ContainsKey(id);
    internal void Fail(Exception error) => _module.Fail(error);
    internal void OwnerBlocked(ZDO zdo, long requested)
    {
        _blockedOwner++;
        _diagnostics.Sample(Time.unscaledTime, "ownership", () => $"ownership transfer prevented; {Describe(zdo)}; current owner={zdo.GetOwner()}; requested owner={requested}; retained owner={ServerId}");
    }
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
    internal void WeatherGate(WearNTear wear)
    {
        _weatherGates++;
        if (!_diagnostics.DebugEnabled) return;
        int shield = (int)_shield.GetValue(wear);
        if (wear.m_noRoofWear && wear.GetHealthPercentage() > 0.5f &&
            !ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield) && wear.IsWet()) _wetOpportunities++;
    }
    internal WearObservation? BeginWear(WearNTear wear, ZNetView view, bool protectedPiece, float time)
    {
        if (!_diagnostics.DebugEnabled) return null;
        var zdo = view.GetZDO();
        var reason = Eligibility(zdo, ZNetScene.instance);
        // Observe only structural weather-wearing controls, not every world object.
        if (!protectedPiece && reason != "world-created" && reason != "eligible") return null;
        if (!_diagnostics.TryWearSample(Time.unscaledTime, zdo.m_uid.ToString(), protectedPiece)) return null;
        int shield = (int)_shield.GetValue(wear);
        bool shielded = ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield);
        return new WearObservation
        {
            Object = Describe(zdo), Protected = protectedPiece, Owner = view.IsOwner(),
            Warmup = !(bool)_shouldUpdate.Invoke(wear, new object[] { time }),
            Before = zdo.GetFloat(ZDOVars.s_health, wear.m_health),
            Context = $"eligibility={reason}; owner={zdo.GetOwner()}; server={ServerId}; native weather wear={wear.m_noRoofWear}; wear time={time:F1}; environment={EnvMan.instance?.GetCurrentEnvironment()?.m_name ?? "unavailable"}; server reference={ZNet.instance.GetReferencePosition()}; env wet={EnvMan.IsWet()}; cached roof={_roof.GetValue(wear)}; shielded={shielded}; underwater={_underWater.Invoke(wear, Array.Empty<object>())}; cached health fraction={wear.GetHealthPercentage():F3}; max health={wear.m_health}; rain timer={_rainTimer.GetValue(wear)}; support wear={wear.m_noSupportWear}; removable={_canBeRemoved.Invoke(wear, Array.Empty<object>())}; NoBuildingFall={ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall)}"
        };
    }
    internal void EndWear(WearNTear wear, WearObservation? observation, Exception? error)
    {
        if (observation == null) return;
        var view = wear.GetComponent<ZNetView>();
        float? after = view != null && view.IsValid() ? view.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) : (float?)null;
        _diagnostics.Wear(observation, after, error != null);
    }
    internal WearObservation? BeginCommand(WearNTear wear, string method)
    {
        var view = wear.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !Protects(view.GetZDO().m_uid)) return null;
        _nativeCommands++;
        if (!_diagnostics.ReserveCommand(Time.unscaledTime)) return null;
        var zdo = view.GetZDO();
        return new WearObservation { Object = Describe(zdo), Before = zdo.GetFloat(ZDOVars.s_health, wear.m_health),
            Context = $"method={method}; local owner={view.IsOwner()}; owner={zdo.GetOwner()}; server={ServerId}" };
    }
    internal void EndCommand(WearNTear wear, WearObservation? observation, Exception? error)
    {
        if (observation == null) return;
        var view = wear.GetComponent<ZNetView>();
        float? health = view != null && view.IsValid() ? view.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) : (float?)null;
        _diagnostics.Command(observation, health, error != null);
    }
    internal void SupportPass(WearNTear wear)
    {
        var view = wear.GetComponent<ZNetView>();
        if (view != null && view.IsValid() && view.IsOwner() && Protects(view.GetZDO().m_uid)) _supportPasses++;
    }
    private string Describe(ZDO zdo)
        => $"object={zdo.m_uid}; prefab={ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name ?? "unknown"}; position={zdo.GetPosition()}; sector={zdo.GetSector()}";
    private static void Count(Dictionary<string, int> counts, string reason)
    { counts.TryGetValue(reason, out int count); counts[reason] = count + 1; }

    public void Tick(float now)
    {
        if (now < _nextRefresh) return;
        _nextRefresh = now + 1f;
        var manager = ZDOMan.instance; var scene = ZNetScene.instance; var zones = ZoneSystem.instance;
        if (manager == null || scene == null || zones == null) return;
        _near.Clear(); _distant.Clear(); _nearIds.Clear(); _distantIds.Clear(); _regions.Clear(); _positions.Clear(); _readyContexts.Clear();
        bool limited = false;
        _peers = _readyPeers = _characterWaits = 0; _exclusions.Clear(); _releaseReasons.Clear();
        foreach (var peer in ZNet.instance.GetPeers())
        {
            _peers++;
            if (!peer.IsReady()) continue;
            _readyPeers++;
            if (peer.m_characterID.IsNone()) { _characterWaits++; continue; }
            var character = manager.GetZDO(peer.m_characterID);
            if (character == null || !character.IsValid() || character.GetOwner() != peer.m_uid) { _characterWaits++; continue; }
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
            string reason = zdo == null || !zdo.IsValid() ? "destroyed/invalid" : !_nearIds.Contains(pair.Key) ? "outside retained region" :
                Eligibility(zdo, scene) != "eligible" ? Eligibility(zdo, scene) : !Ready(zdo, scene, zones) ? "context lost" : "";
            if (reason != "")
            { _release.Add(pair.Key); Count(_releaseReasons, reason); _diagnostics.Sample(now, "ownership", () => $"server control released; object={pair.Key}; reason={reason}; previous owner={pair.Value.GetOwner()}"); }
            else if (zdo!.GetOwner() != ServerId)
            {
                _ownerLosses++;
                _diagnostics.Sample(now, "ownership", () => $"ownership loss observed; {Describe(zdo)}; observed owner={zdo.GetOwner()}; reasserting server={ServerId}; cause unknown");
                zdo.SetOwner(ServerId); manager.ForceSendZDO(zdo.m_uid);
            }
        }
        foreach (var id in _release) Release(id);
        _eligible = _waiting = _waitingTerrain = _waitingObjects = _waitingInstance = 0;
        foreach (var zdo in _near)
        {
            string exclusion = Eligibility(zdo, scene);
            if (exclusion != "eligible")
            {
                Count(_exclusions, exclusion);
                if (exclusion != "not a wear piece") _diagnostics.Sample(now, "eligibility", () => $"excluded; {Describe(zdo)}; reason={exclusion}; creator present={zdo.GetLong(ZDOVars.s_creator) != 0}; health={zdo.GetFloat(ZDOVars.s_health, -1f)}");
                continue;
            }
            _eligible++;
            if (_protected.ContainsKey(zdo.m_uid)) continue;
            if (_protected.Count >= WeatherGuardRules.MaximumProtected) { limited = true; continue; }
            bool instanceReady = scene.FindInstance(zdo) != null;
            bool terrainReady = TerrainReady(zdo, zones);
            bool objectsReady = terrainReady && ContextReady(zdo, scene, zones);
            if (!WeatherGuardRules.MayAcquire(instanceReady, terrainReady, objectsReady, true))
            {
                _waiting++; if (!terrainReady) _waitingTerrain++; if (!instanceReady) _waitingInstance++; if (terrainReady && !objectsReady) _waitingObjects++;
                _diagnostics.Sample(now, "context", () => $"context blocked; {Describe(zdo)}; instance={instanceReady}; terrain={terrainReady}; surrounding objects={objectsReady}; locations generated={zones.LocationsGenerated}; server active terrain={zones.IsActiveAreaLoaded()}; {ContextBlocker(zdo, scene, zones)}");
                continue;
            }
            // Insert before changing owner so ownership guards cover the whole transition.
            _protected.Add(zdo.m_uid, zdo);
            // A server instance may precede the client's creator-data update. Refresh only
            // the server's cached attribution; never assign or transfer persistent creators.
            var piece = scene.FindInstance(zdo).GetComponent<Piece>();
            AccessTools.Field(typeof(Piece), "m_creator").SetValue(piece, zdo.GetLong(ZDOVars.s_creator));
            AccessTools.Field(typeof(Piece), "m_creatorPlatformUserIDIndex").SetValue(piece, zdo.GetInt(ZDOVars.s_creatorIndex, -1));
            long previousOwner = zdo.GetOwner();
            zdo.SetOwner(ServerId);
            manager.ForceSendZDO(zdo.m_uid);
            _acquired++;
            _diagnostics.Sample(now, () => $"server control acquired; {Describe(zdo)}; creator preserved=true; health={zdo.GetFloat(ZDOVars.s_health, -1f)}; previous owner={previousOwner}; owner={zdo.GetOwner()}; wear enabled={scene.FindInstance(zdo).GetComponent<WearNTear>().enabled}");
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
            BlockedBroadcast = _blockedBroadcast, Commands = _commands, WearPasses = _wearPasses, Limited = limited,
            Peers = _peers, ReadyPeers = _readyPeers, CharacterWaits = _characterWaits,
            Exclusions = new Dictionary<string, int>(_exclusions), ReleaseReasons = new Dictionary<string, int>(_releaseReasons),
            OwnerLosses = _ownerLosses, WeatherGates = _weatherGates, WetOpportunities = _wetOpportunities,
            NativeCommands = _nativeCommands, SupportPasses = _supportPasses
        });
    }
    private static string Eligibility(ZDO zdo, ZNetScene scene)
    {
        if (!zdo.IsValid()) return "invalid world data";
        var prefab = scene.GetPrefab(zdo.GetPrefab());
        // Container inventory changes require client ownership and must not be filtered.
        if (prefab == null) return "missing prefab";
        if (!prefab.TryGetComponent<Piece>(out var piece) || !prefab.TryGetComponent<WearNTear>(out var wear)) return "not a wear piece";
        return WeatherGuardRules.Exclusion(zdo.GetLong(ZDOVars.s_creator), zdo.Persistent, wear.m_staticPosition,
                wear.m_noRoofWear, zdo.GetFloat(ZDOVars.s_health, wear.m_health),
                piece.m_category == Piece.PieceCategory.BuildingWorkbench || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth,
                prefab.GetComponent<Container>() != null);
    }
    private string ContextBlocker(ZDO zdo, ZNetScene scene, ZoneSystem zones)
    {
        var sector = zdo.GetSector();
        for (int x = sector.x - 1; x <= sector.x + 1; x++)
        for (int y = sector.y - 1; y <= sector.y + 1; y++)
            if (!zones.IsZoneLoaded(new Vector2s(x, y))) return $"first missing terrain=({x},{y})";
        var objects = new List<ZDO>();
        _manager.FindSectorObjects(sector, new SimulationDistance(1, 0), objects);
        foreach (var candidate in objects)
            if (scene.GetPrefab(candidate.GetPrefab()) != null && scene.FindInstance(candidate) == null)
                return $"first missing context object: {Describe(candidate)}; retained near={_nearIds.Contains(candidate.m_uid)}; retained distant={_distantIds.Contains(candidate.m_uid)}";
        return "no missing context object found in diagnostic sample";
    }
    private static bool TerrainReady(ZDO zdo, ZoneSystem zones)
    {
        var sector = zdo.GetSector();
        // Support can cross sector edges: do not take ownership with only the central terrain loaded.
        for (int x = sector.x - 1; x <= sector.x + 1; x++)
        for (int y = sector.y - 1; y <= sector.y + 1; y++)
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
        if (zdo.IsValid()) _manager.ForceSendZDO(id);
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
