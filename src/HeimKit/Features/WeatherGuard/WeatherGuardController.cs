using HeimKit.Features.WeatherGuard.Models;
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;
using HeimKit.Shared;
using HeimKit.Configuration;
using HeimKit.Shared.Players;

namespace HeimKit.Features.WeatherGuard;

// The server runs vanilla combat/support for protected pieces. Clients keep ordinary RPCs.
internal sealed class WeatherGuardController : IDisposable
{
    private readonly WeatherGuard _module;
    private readonly IPlayerPresence _presence;
    private readonly ManualLogSource _log;
    private readonly WeatherGuardDiagnostics _diagnostics;
    private readonly bool _protectRain, _protectUnderwater;
    private readonly HashSet<string> _excludedPrefabs;
    private float _lastScopeScan = -1;
    private bool _scopeLimited;
    private readonly FieldInfo _rainWet = AccessTools.Field(typeof(WearNTear), "m_rainWet");
    private readonly ZDOMan _manager;
    private readonly WeatherGuardPatches _patches;
    private readonly List<ZDO> _near = new(), _distant = new(), _scratch = new(), _farScratch = new();
    private readonly HashSet<ZDOID> _nearIds = new(), _distantIds = new();
    private readonly HashSet<ZDOID> _instanceSeen = new();
    private readonly HashSet<Vector2s> _regions = new();
    private readonly List<Vector3> _positions = new();
    private readonly Dictionary<ZDOID, ZDO> _protected = new();
    private readonly Dictionary<Vector2s, bool> _readyContexts = new();
    private readonly Dictionary<int, bool> _contextPrefabs = new();
    private readonly List<ZDO> _contextObjects = new();
    private readonly List<ZDOID> _release = new();
    private readonly Dictionary<string, int> _exclusions = new();
    private readonly Dictionary<string, int> _releaseReasons = new();
    private readonly Dictionary<string, int> _releaseTotals = new();
    private readonly Dictionary<ZDOID, RainEvidence> _rainEvidence = new();
    private readonly HashSet<ZDOID> _observedCandidates = new();
    private readonly Dictionary<ZDOID, string> _candidateReasons = new();
    private int _rainProbeCursor;
    private int _exposureProbeCursor;
    private readonly WeatherGuardExposureTracking _exposure = new();
    private readonly List<ZDO> _eligiblePieces = new();
    private readonly Dictionary<string, int> _playerBiomes = new();
    private bool _candidateScanStarted;
    private ZDOID _recentPlayerCandidate;
    private readonly FieldInfo _roof = AccessTools.Field(typeof(WearNTear), "m_haveRoof");
    private readonly FieldInfo _shield = AccessTools.Field(typeof(WearNTear), "m_shieldChangeID");
    private readonly FieldInfo _rainTimer = AccessTools.Field(typeof(WearNTear), "m_rainTimer");
    private readonly FieldInfo _creationTime = AccessTools.Field(typeof(WearNTear), "m_createTime");
    private readonly FieldInfo _coverTimer = AccessTools.Field(typeof(WearNTear), "m_updateCoverTimer");
    private readonly MethodInfo _shouldUpdate = AccessTools.Method(typeof(WearNTear), "ShouldUpdate");
    private readonly MethodInfo _underWater = AccessTools.Method(typeof(WearNTear), "IsUnderWater");
    private readonly MethodInfo _canBeRemoved = AccessTools.Method(typeof(WearNTear), "CanBeRemoved");
    private readonly MethodInfo _createZones = typeof(ZoneSystem).GetMethod("CreateLocalZones", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly FieldInfo _instancesField = AccessTools.Field(typeof(ZNetScene), "m_instances");
    private float _nextRefresh, _nextTerrain;
    private bool _announced;
    private int _eligible, _waiting, _waitingTerrain, _waitingObjects, _waitingInstance;
    private long _acquired, _released, _blockedData, _blockedOwner, _blockedBroadcast, _commands, _wearPasses;
    private long _ownerLosses, _weatherGates, _wetOpportunities;
    private long _weatherSuppressedTotal, _confirmedPreventions;
    private readonly Dictionary<string, long> _decisions = new();
    private long _nativeCommands, _supportPasses;
    private int _peers, _readyPeers, _characterWaits;
    internal static WeatherGuardController? Active { get; private set; }
    internal long ServerId => ZDOMan.GetSessionID();
    internal bool Running => Active == this && _module.IsActive && ReferenceEquals(_manager, ZDOMan.instance) &&
        (_protectRain || _protectUnderwater) && ZNetScene.instance != null && ZNet.instance != null && ZNet.instance.IsDedicated() && ZNet.instance.IsServer();

    public WeatherGuardController(WeatherGuard module, ManualLogSource log, DebugLoggingPolicy debug, DebugLogLevel level, bool logWorldCreated, bool protectRain, bool protectUnderwater, string excludedPrefabs, IPlayerPresence presence)
    {
        _presence = presence;
        _protectRain = protectRain; _protectUnderwater = protectUnderwater;
        _excludedPrefabs = WeatherGuardRules.ParseExcludedPrefabs(excludedPrefabs, out int rejected);
        if (rejected > 0) DiagnosticLog.Write(log, "WeatherGuard", DiagnosticEvent.Lifecycle,
            $"Ignored {rejected} invalid or over-limit prefab exclusions. Review WeatherGuard.ExcludedPrefabs.", new (string, object?)[] { ("rejected_entries", rejected) }, LogLevel.Warning);
        _module = module; _log = log; _diagnostics = new WeatherGuardDiagnostics(log, debug, level, logWorldCreated); _manager = ZDOMan.instance; _patches = new WeatherGuardPatches(); }
    public void Install()
    {
        if (_rainWet == null || _createZones == null || _instancesField == null || _roof == null || _shield == null || _rainTimer == null || _coverTimer == null || _creationTime == null ||
            _shouldUpdate == null || _underWater == null || _canBeRemoved == null) throw new MissingMemberException("WeatherGuard terrain/instance/wear evidence APIs unavailable.");
        var unknown = new List<string>();
        foreach (string name in _excludedPrefabs) if (ZNetScene.instance.GetPrefab(name) == null) unknown.Add(name);
        if (unknown.Count > 0) DiagnosticLog.Write(_log, "WeatherGuard", DiagnosticEvent.Lifecycle,
            $"{unknown.Count} excluded prefab identifiers were not found. Review WeatherGuard.ExcludedPrefabs; matching remains exact.", new (string, object?)[]
            { ("unknown_prefabs", unknown.Count), ("samples", string.Join(",", unknown.GetRange(0, Math.Min(6, unknown.Count)))) }, LogLevel.Warning);
        Active = this;
        try { _patches.Install(); } catch { Dispose(); throw; }
    }
    internal bool Protects(ZDOID id) => Running && _protected.ContainsKey(id);
    internal void Fail(Exception error) => _module.Fail(error);
    internal void OwnerBlocked(ZDO zdo, long requested)
    {
        _blockedOwner++;
        // Routine rejected transfers are summarized by counters, not repeated item messages.
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
    internal bool SuppressWeather(WearNTear wear, bool native)
    {
        if (_protectRain && _protectUnderwater) return native;
        return WeatherGuardRules.SuppressWeatherDamage(native, _protectRain, _protectUnderwater,
            (bool)_rainWet.GetValue(wear), (bool)_underWater.Invoke(wear, Array.Empty<object>()));
    }
    internal bool WeatherDamageMayApply(WearNTear wear)
    {
        bool allowed = (bool)_canBeRemoved.Invoke(wear, Array.Empty<object>()) && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall);
        if (!allowed && _diagnostics.StepsEnabled)
        { _decisions.TryGetValue("native_final_damage_disabled", out long count); _decisions["native_final_damage_disabled"] = count + 1; }
        return allowed;
    }
    internal string? PiecePrefab(ZDO zdo) => ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name;
    internal void WeatherDamageSuppressed(int count, string? singleId = null, string? singlePrefab = null)
    { _confirmedPreventions += count; _diagnostics.WeatherDamageSuppressed(count, singleId, singlePrefab); }
    internal void WearConditions(WearNTear wear, float time)
    {
        if (!_diagnostics.StepsEnabled) return;
        int shield = (int)_shield.GetValue(wear);
        bool underwater = (bool)_underWater.Invoke(wear, Array.Empty<object>());
        string stage = !(bool)_shouldUpdate.Invoke(wear, new object[] { time }) ? "waiting_warmup" :
            ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield) ? "shielded" :
            wear.GetHealthPercentage() <= 0.5f ? "health_at_or_below_half" :
            !underwater && (bool)_roof.GetValue(wear) ? "native_cached_roof_covered" :
            !underwater && !EnvMan.IsWet() ? "native_environment_dry" :
            (float)_rainTimer.GetValue(wear) == 0f ? "starting_wet_timer" :
            time - (float)_rainTimer.GetValue(wear) <= 60f ? "waiting_wet_timer" : "native_conditions_met";
        _decisions.TryGetValue(stage, out long count); _decisions[stage] = count + 1;
    }
    internal void WeatherGate(WearNTear wear, bool branchBypassed)
    {
        _weatherGates++;
        if (branchBypassed) _weatherSuppressedTotal++;
        if (!_diagnostics.DebugEnabled) return;
        int shield = (int)_shield.GetValue(wear);
        bool wet = wear.m_noRoofWear && wear.GetHealthPercentage() > 0.5f &&
            !ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield) && wear.IsWet();
        if (wet) _wetOpportunities++;
        var zdo = wear.GetComponent<ZNetView>().GetZDO();
        if (wet) TrackRain(zdo, wear, "eligible");
        if (_rainEvidence.TryGetValue(zdo.m_uid, out var history)) history.ObserveGate(Time.time, wet, branchBypassed);
    }
    internal WearObservation? BeginWear(WearNTear wear, ZNetView view, bool protectedPiece, float time)
    {
        if (!_diagnostics.DebugEnabled) return null;
        var zdo = view.GetZDO();
        var reason = Eligibility(zdo, ZNetScene.instance);
        // Observe only structural weather-wearing controls, not every world object.
        if (!protectedPiece && reason != "world-created" && reason != "eligible") return null;
        if (reason == "world-created" && !_diagnostics.LogWorldCreatedItems) return null;
        bool exposed = !(bool)_roof.GetValue(wear) || (bool)_underWater.Invoke(wear, Array.Empty<object>());
        if (exposed) TrackRain(zdo, wear, reason);
        if (!_diagnostics.TryWearSample(Time.unscaledTime, zdo.m_uid.ToString(), protectedPiece, exposed)) return null;
        int shield = (int)_shield.GetValue(wear);
        bool shielded = ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield);
        return new WearObservation
        {
            Identity = Identity(zdo), Protected = protectedPiece, Owner = view.IsOwner(),
            Warmup = !(bool)_shouldUpdate.Invoke(wear, new object[] { time }),
            Before = zdo.GetFloat(ZDOVars.s_health, wear.m_health),
            Fields = new Dictionary<string, object?>
            {
                ["native_rain_wet"] = _rainWet.GetValue(wear),
                ["eligibility"] = reason, ["processor"] = view.IsOwner() ? "server" : zdo.GetOwner() == 0 ? "none" : "client",
                ["server_environment"] = EnvMan.instance?.GetCurrentEnvironment()?.m_name, ["environment_wet"] = EnvMan.IsWet(),
                ["cached_roof"] = _roof.GetValue(wear), ["fresh_roof"] = WearNTear.RoofCheck(wear.transform, wear.transform.position, out _, wear.m_roofCheckOffset),
                ["cover_timer"] = _coverTimer.GetValue(wear), ["warmup_remaining_s"] = Math.Max(0, 30 - (time - (float)_creationTime.GetValue(wear))), ["shielded"] = shielded,
                ["underwater"] = _underWater.Invoke(wear, Array.Empty<object>()), ["native_health_fraction"] = wear.GetHealthPercentage(),
                ["maximum_health"] = wear.m_health, ["native_rain_timer_start_game_s"] = _rainTimer.GetValue(wear), ["game_time_s"] = time,
                ["removable"] = _canBeRemoved.Invoke(wear, Array.Empty<object>()), ["no_building_fall"] = ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall)
            }

        };
    }
    internal void EndWear(WearNTear wear, WearObservation? observation, Exception? error)
    {
        if (!_diagnostics.DebugEnabled) return;
        var historyView = wear.GetComponent<ZNetView>();
        if (_diagnostics.DebugEnabled && historyView != null && historyView.IsValid() &&
            _rainEvidence.TryGetValue(historyView.GetZDO().m_uid, out var history))
            UpdateRain(historyView.GetZDO(), wear, history);
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
        return new WearObservation { Protected = true, Owner = view.IsOwner(), Identity = Identity(zdo), Fields = new() { ["command"] = method, ["processor"] = view.IsOwner() ? "server" : "client" }, Before = zdo.GetFloat(ZDOVars.s_health, wear.m_health) };
    }
    internal void EndCommand(WearNTear wear, WearObservation? observation, Exception? error)
    {
        if (!_diagnostics.DebugEnabled) return;
        var watchedView = wear.GetComponent<ZNetView>();
        if (_diagnostics.DebugEnabled && watchedView != null && watchedView.IsValid() && _rainEvidence.TryGetValue(watchedView.GetZDO().m_uid, out var history))
            UpdateRain(watchedView.GetZDO(), wear, history);
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
    private Dictionary<string, object?> Identity(ZDO zdo) => new()
    { ["object"] = zdo.m_uid.ToString(), ["prefab"] = ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name,
      ["position"] = $"{zdo.GetPosition().x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{zdo.GetPosition().y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{zdo.GetPosition().z.ToString(System.Globalization.CultureInfo.InvariantCulture)}" };
    private Dictionary<string, object?> Fields(ZDO zdo, params (string Key, object? Value)[] values)
    { var fields = Identity(zdo); foreach (var value in values) fields[value.Key] = value.Value; return fields; }
    private static string ObservedHealth(ZDO zdo)
    {
        float value = zdo.GetFloat(ZDOVars.s_health, float.NaN);
        return float.IsNaN(value) ? "unknown_or_nan" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void Count(Dictionary<string, int> counts, string reason)
    { counts.TryGetValue(reason, out int count); counts[reason] = count + 1; }

    private void TrackRain(ZDO zdo, WearNTear wear, string reason)
    {
        if (reason == "world-created" && !_diagnostics.LogWorldCreatedItems) return;
        if (_rainEvidence.ContainsKey(zdo.m_uid) || (reason != "eligible" && reason != "world-created")) return;
        int count = 0;
        ZDOID replacement = ZDOID.None;
        foreach (var pair in _rainEvidence) if (pair.Value.Eligibility == reason) { count++; replacement = pair.Key; }
        if (count >= 3)
        {
            if (reason != "eligible" || zdo.m_uid != _recentPlayerCandidate) return;
            _rainEvidence.Remove(replacement);
        }
        float health = zdo.GetFloat(ZDOVars.s_health, wear.m_health);
        var history = new RainEvidence { Identity = Identity(zdo), Eligibility = reason, Started = Time.unscaledTime,
            FirstHealth = health, MaximumHealth = wear.m_health, Health = health, MinimumHealth = health, Owner = zdo.GetOwner(),
            Protected = Protects(zdo.m_uid), Creator = zdo.GetLong(ZDOVars.s_creator) };
        _rainEvidence.Add(zdo.m_uid, history);
        UpdateRain(zdo, wear, history);
    }

    private void UpdateRain(ZDO zdo, WearNTear wear, RainEvidence history)
    {
        history.ObserveState(zdo.GetFloat(ZDOVars.s_health, wear.m_health), zdo.GetOwner(), Protects(zdo.m_uid), zdo.GetLong(ZDOVars.s_creator));
        history.ProtectionReason = history.Protected ? "eligible" : history.Eligibility != "eligible" ? history.Eligibility :
            !TerrainReady(zdo, ZoneSystem.instance) ? "waiting_terrain" :
            !ContextReady(zdo, ZNetScene.instance, ZoneSystem.instance) ? "waiting_objects" :
            _protected.Count >= WeatherGuardRules.MaximumProtected ? "capacity_limit" : "awaiting_acquisition";
        int shield = (int)_shield.GetValue(wear);
        history.Fields = new Dictionary<string, object?>
        {
            ["native_rain_wet"] = _rainWet.GetValue(wear),
            ["processor"] = zdo.GetOwner() == ServerId ? "server" : zdo.GetOwner() == 0 ? "none" : "client",
            ["cached_roof"] = _roof.GetValue(wear), ["cover_timer"] = _coverTimer.GetValue(wear),
            ["piece_biome"] = Biome(zdo.GetPosition()),
            ["environment_wet"] = EnvMan.IsWet(), ["server_environment"] = EnvMan.instance?.GetCurrentEnvironment()?.m_name,
            ["underwater"] = _underWater.Invoke(wear, Array.Empty<object>()),
            ["shielded"] = ShieldGenerator.IsInsideShieldCached(wear.transform.position, ref shield),
            ["health_above_half"] = wear.GetHealthPercentage() > 0.5f, ["removable"] = _canBeRemoved.Invoke(wear, Array.Empty<object>()),
            ["no_building_fall"] = ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBuildingFall),
            ["warmup"] = !(bool)_shouldUpdate.Invoke(wear, new object[] { Time.time }),
            ["warmup_remaining_s"] = Math.Max(0, 30 - (Time.time - (float)_creationTime.GetValue(wear)))
        };
        if (history.LastGateTime >= 0 && Time.time - history.LastGateTime > 10) { history.WetSeconds = 0; history.LastGateTime = -1; }
    }

    private void RefreshRainEvidence(ZNetScene scene)
    {
        if (!_diagnostics.DebugEnabled) return;
        var removed = new List<ZDOID>();
        foreach (var pair in _rainEvidence)
        {
            var zdo = _manager.GetZDO(pair.Key);
            if (zdo == null || !zdo.IsValid() || !_nearIds.Contains(pair.Key))
            {
                _diagnostics.Record(Time.unscaledTime, "rain ended", () => ("Rain tracking ended", new() { ["object"] = pair.Key.ToString(), ["reason"] = zdo == null ? "missing_world_record" : !zdo.IsValid() ? "invalid_world_record" : "outside_retained_region", ["last_health"] = pair.Value.Health, ["minimum_health"] = pair.Value.MinimumHealth }));
                removed.Add(pair.Key); continue;
            }
            string currentEligibility = Eligibility(zdo, scene);
            if (currentEligibility != pair.Value.Eligibility)
            {
                _diagnostics.Record(Time.unscaledTime, "rain ended", () => ("Rain tracking role changed", new()
                { ["object"] = pair.Key.ToString(), ["previous_eligibility"] = pair.Value.Eligibility, ["eligibility"] = currentEligibility,
                  ["last_health"] = pair.Value.Health, ["minimum_health"] = pair.Value.MinimumHealth }));
                removed.Add(pair.Key); continue;
            }
            var view = scene.FindInstance(zdo);
            if (view != null && view.TryGetComponent<WearNTear>(out var wear)) UpdateRain(zdo, wear, pair.Value);
            else
            {
                pair.Value.ObserveState(zdo.GetFloat(ZDOVars.s_health, pair.Value.MaximumHealth), zdo.GetOwner(), Protects(zdo.m_uid), zdo.GetLong(ZDOVars.s_creator));
                pair.Value.WetSeconds = 0; pair.Value.LastGateTime = -1;
                pair.Value.ProtectionReason = pair.Value.Eligibility == "eligible" ? "waiting_instance" : pair.Value.Eligibility;
                pair.Value.Fields = new() { ["server_instance"] = false, ["native_conditions"] = null };
            }
        }
        foreach (var id in removed) _rainEvidence.Remove(id);
        // Probe at most twelve structural candidates per scan. Fresh geometry detects
        // exposed pieces even while the vanilla cover cache still says roofed.
        int probes = 0;
        for (int visited = 0; visited < _near.Count && probes < 12; visited++)
        {
            _rainProbeCursor %= _near.Count;
            var zdo = _near[_rainProbeCursor++];
            string reason = Eligibility(zdo, scene);
            if (reason != "world-created" || !_diagnostics.LogWorldCreatedItems) continue;
            var view = scene.FindInstance(zdo);
            if (view == null || !view.TryGetComponent<WearNTear>(out var wear)) continue;
            probes++;
            bool roof = WearNTear.RoofCheck(wear.transform, wear.transform.position, out _, wear.m_roofCheckOffset);
            if (!roof) TrackRain(zdo, wear, reason);
            if (_rainEvidence.TryGetValue(zdo.m_uid, out var evidence)) { evidence.FreshRoof = roof; evidence.FreshRoofAt = Time.unscaledTime; }
        }
    }

    private static string Biome(Vector3 position)
        => WorldGenerator.instance == null ? "unknown" : WorldGenerator.instance.GetBiome(position).ToString();
    private void RefreshExposure(ZNetScene scene, float now)
    {
        var managed = new HashSet<string>();
        foreach (var zdo in _eligiblePieces)
        {
            var view = scene.FindInstance(zdo);
            if (Protects(zdo.m_uid) && view != null && view.IsValid() && view.IsOwner()) managed.Add(zdo.m_uid.ToString());
        }
        _exposure.Retain(System.Linq.Enumerable.Select(_eligiblePieces, zdo => zdo.m_uid.ToString()), managed.Contains);
        int probes = 0;
        for (int visited = 0; visited < _eligiblePieces.Count && probes < WeatherGuardExposureTracking.ProbeLimitPerScan; visited++)
        {
            _exposureProbeCursor %= _eligiblePieces.Count;
            var zdo = _eligiblePieces[_exposureProbeCursor++];
            var view = scene.FindInstance(zdo);
            if (view == null || !view.IsValid() || !ContextReady(zdo, scene, ZoneSystem.instance) ||
                !view.TryGetComponent<WearNTear>(out var wear)) continue;
            probes++;
            bool roof = WearNTear.RoofCheck(wear.transform, wear.transform.position, out _, wear.m_roofCheckOffset);
            bool water = (bool)_underWater.Invoke(wear, Array.Empty<object>());
            _exposure.Observe(zdo.m_uid.ToString(), roof, water, managed.Contains(zdo.m_uid.ToString()),
                (bool)_roof.GetValue(wear), Biome(zdo.GetPosition()), now);
            if (!roof && _diagnostics.DebugEnabled) TrackRain(zdo, wear, "eligible");
            if (_rainEvidence.TryGetValue(zdo.m_uid, out var evidence)) { evidence.FreshRoof = roof; evidence.FreshRoofAt = now; }
        }
    }

    public void Tick(float now)
    {
        if (now < _nextRefresh) return;
        _nextRefresh = now + 1f;
        _eligiblePieces.Clear(); _playerBiomes.Clear();
        var manager = ZDOMan.instance; var scene = ZNetScene.instance; var zones = ZoneSystem.instance;
        if (manager == null || scene == null || zones == null) return;
        _near.Clear(); _distant.Clear(); _nearIds.Clear(); _distantIds.Clear(); _regions.Clear(); _positions.Clear(); _readyContexts.Clear();
        bool limited = false;
        _peers = _readyPeers = _characterWaits = 0; _exclusions.Clear(); _releaseReasons.Clear();
        foreach (var peer in _presence.Peers)
        {
            _peers++;
            if (!peer.IsReady()) continue;
            _readyPeers++;
            if (peer.m_characterID.IsNone()) { _characterWaits++; continue; }
            var character = manager.GetZDO(peer.m_characterID);
            if (character == null || !character.IsValid() || character.GetOwner() != peer.m_uid) { _characterWaits++; continue; }
            var position = character.GetPosition();
            Count(_playerBiomes, Biome(position));
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
            { _release.Add(pair.Key); Count(_releaseReasons, reason); Count(_releaseTotals, reason); }
            else if (zdo!.GetOwner() != ServerId)
            {
                _ownerLosses++;
                _diagnostics.Record(now, "ownership", () => ("Owner change observed; server ownership reasserted; cause unknown", Fields(zdo, ("observed_owner", zdo.GetOwner()), ("server_owner", ServerId))));
                zdo.SetOwner(ServerId); manager.ForceSendZDO(zdo.m_uid);
            }
        }
        foreach (var id in _release) Release(id);
        _eligible = _waiting = _waitingTerrain = _waitingObjects = _waitingInstance = 0;
        if (_diagnostics.DebugEnabled)
        {
            _observedCandidates.IntersectWith(_nearIds);
            var staleReasons = new List<ZDOID>();
            foreach (var id in _candidateReasons.Keys) if (!_nearIds.Contains(id)) staleReasons.Add(id);
            foreach (var id in staleReasons) _candidateReasons.Remove(id);
        }
        foreach (var zdo in _near)
        {
            string exclusion = Eligibility(zdo, scene);
            string? priorReason = null;
            bool reasonChanged = _diagnostics.DebugEnabled && (!_candidateReasons.TryGetValue(zdo.m_uid, out priorReason) || priorReason != exclusion);
            if (reasonChanged && priorReason != null)
            {
                if (exclusion == "eligible") _recentPlayerCandidate = zdo.m_uid;
                _diagnostics.Record(now, "discovery", () => ("Eligibility changed", Fields(zdo, ("previous_eligibility", priorReason), ("eligibility", exclusion))));
            }
            if (_diagnostics.DebugEnabled && (exclusion != "not a wear piece" || _candidateReasons.ContainsKey(zdo.m_uid))) _candidateReasons[zdo.m_uid] = exclusion;
            bool firstObserved = false;
            if (_diagnostics.DebugEnabled && (exclusion == "eligible" || exclusion == "world-created") && _observedCandidates.Add(zdo.m_uid))
            {
                firstObserved = true;
                if (_candidateScanStarted && exclusion == "eligible") _recentPlayerCandidate = zdo.m_uid;
                _diagnostics.Record(now, _candidateScanStarted ? "discovery" : "initial discovery", () => ("Structural candidate first observed; creation time unknown", Fields(zdo, ("eligibility", exclusion), ("discovery", _candidateScanStarted ? "later_scan" : "initial_scan"))));
            }
            if (exclusion != "eligible")
            {
                Count(_exclusions, exclusion);
                if (exclusion != "not a wear piece" && reasonChanged && !firstObserved) _diagnostics.Record(now, "eligibility", () => ("Excluded from protection", Fields(zdo, ("reason", exclusion), ("stored_health", ObservedHealth(zdo)))));
                continue;
            }
            _eligible++;
            _eligiblePieces.Add(zdo);
            if (_protected.ContainsKey(zdo.m_uid)) continue;
            if (_protected.Count >= WeatherGuardRules.MaximumProtected) { limited = true; continue; }
            bool instanceReady = scene.FindInstance(zdo) != null;
            bool terrainReady = TerrainReady(zdo, zones);
            bool objectsReady = terrainReady && ContextReady(zdo, scene, zones);
            if (!WeatherGuardRules.MayAcquire(instanceReady, terrainReady, objectsReady, true))
            {
                _waiting++; if (!terrainReady) _waitingTerrain++; if (!instanceReady) _waitingInstance++; if (terrainReady && !objectsReady) _waitingObjects++;
                _diagnostics.Record(now, "context", () =>
                {
                    var evidence = Fields(zdo, ("instance_loaded", instanceReady), ("terrain_loaded", terrainReady),
                        ("surrounding_objects_loaded", objectsReady), ("locations_generated", zones.LocationsGenerated));
                    foreach (var dependency in ContextBlocker(zdo, scene, zones)) evidence[dependency.Key] = dependency.Value;
                    return ("Waiting for server context", evidence);
                });
                continue;
            }
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

            // Successful acquisition/release counts are grouped by diagnostics interval.
        }
        if (_protected.Count > 0 && !_announced)
        {
            _announced = true;
            _diagnostics.InitialAcquisition(_protected.Count);
        }
        _diagnostics.CapacityState(limited);
        _candidateScanStarted |= _near.Count > 0;
        _lastScopeScan = now; _scopeLimited = limited;
        if (_diagnostics.StepsEnabled) RefreshExposure(scene, now);
        RefreshRainEvidence(scene);
        _diagnostics.Report(now, () => new WeatherGuardStatus
        {
            RainProtection = _protectRain, UnderwaterProtection = _protectUnderwater, Protected = _protected.Count, Eligible = _eligible, Waiting = _waiting,
            WaitingTerrain = _waitingTerrain, WaitingInstance = _waitingInstance, WaitingObjects = _waitingObjects,
            Regions = _regions.Count, Nearby = _near.Count, Distant = _distant.Count,
            Acquired = _acquired, Released = _released, BlockedData = _blockedData, BlockedOwner = _blockedOwner,
            BlockedBroadcast = _blockedBroadcast, Commands = _commands, WearPasses = _wearPasses, Limited = limited,
            Peers = _peers, ReadyPeers = _readyPeers, CharacterWaits = _characterWaits,
            Exclusions = new Dictionary<string, int>(_exclusions), ReleaseReasons = new Dictionary<string, int>(_releaseReasons), ReleaseTotals = new Dictionary<string, int>(_releaseTotals),
            OwnerLosses = _ownerLosses, WeatherGates = _weatherGates, WetOpportunities = _wetOpportunities,
            Decisions = new Dictionary<string, long>(_decisions), WeatherSuppressed = _weatherSuppressedTotal, ConfirmedPreventions = _confirmedPreventions,
            NativeCommands = _nativeCommands, SupportPasses = _supportPasses
        }, () => _rainEvidence.Values, ProtectionStatus, () =>
        {
            var counts = _exposure.Counts(_eligible, now);
            counts["player_biomes"] = string.Join(",", System.Linq.Enumerable.Select(_playerBiomes, pair => pair.Key + ":" + pair.Value));
            counts["server_environment"] = EnvMan.instance?.GetCurrentEnvironment()?.m_name;
            counts["server_environment_wet"] = EnvMan.IsWet();
            counts["weather_immune_skipped_now"] = _exclusions.TryGetValue("weather immune", out int immune) ? immune : 0;
            return counts;
        });

    }
    internal CoverageSnapshot Coverage(float now)
    {
        var result = new CoverageSnapshot { State = _regions.Count == 0 ? "no_ready_player_regions" : "ready", Observed = _lastScopeScan >= 0 && _regions.Count > 0,
            ScanAge = _lastScopeScan < 0 ? null : Math.Max(0, now - _lastScopeScan), Regions = _regions.Count,
            NearbyRecords = _near.Count, DistantRecords = _distant.Count, Limited = _scopeLimited,
            RainEnabled = _protectRain, UnderwaterEnabled = _protectUnderwater };
        if (!result.Observed) return result;
        foreach (var zdo in _near)
        {
            string reason = Eligibility(zdo, ZNetScene.instance);
            var view = ZNetScene.instance.FindInstance(zdo);
            bool loaded = view != null && view.IsValid();
            bool terrain = reason == "eligible" && TerrainReady(zdo, ZoneSystem.instance);
            bool context = terrain && ContextReady(zdo, ZNetScene.instance, ZoneSystem.instance);
            bool managed = loaded && context && Protects(zdo.m_uid) && view!.IsOwner();
            string waiting = !terrain ? "waiting_terrain" : !context ? "waiting_objects" :
                _protected.Count >= WeatherGuardRules.MaximumProtected ? "capacity_limit" : "waiting_server_ownership";
            result.Add(reason, managed, loaded, waiting);
        }
        return result;
    }
    private IEnumerable<ProtectionObservation> ProtectionStatus()
    {
        foreach (var zdo in _near)
        {
            string eligibility = Eligibility(zdo, ZNetScene.instance);
            if (eligibility != "eligible" && eligibility != "world-created" && eligibility != "excluded_prefab" && eligibility != "weather_protection_disabled") continue;
            bool worldCreated = zdo.GetLong(ZDOVars.s_creator) == 0;
            if (worldCreated && !_diagnostics.LogWorldCreatedItems) continue;
            bool managed = Protects(zdo.m_uid);
            string reason = eligibility != "eligible" ? eligibility : managed ? "eligible" :
                ZNetScene.instance.FindInstance(zdo) == null ? "waiting_instance" :
                !TerrainReady(zdo, ZoneSystem.instance) ? "waiting_terrain" :
                !ContextReady(zdo, ZNetScene.instance, ZoneSystem.instance) ? "waiting_objects" :
                _protected.Count >= WeatherGuardRules.MaximumProtected ? "capacity_limit" : "awaiting_acquisition";
            yield return new ProtectionObservation { Identity = Identity(zdo), Protected = managed, RainProtection = _protectRain, UnderwaterProtection = _protectUnderwater, Reason = reason,
                Origin = worldCreated ? "world_created" : "player_built" };
        }
    }
    private string Eligibility(ZDO zdo, ZNetScene scene)
    {
        if (!zdo.IsValid()) return "invalid world data";
        var prefab = scene.GetPrefab(zdo.GetPrefab());
        // Container inventory changes require client ownership and must not be filtered.
        if (prefab == null) return "missing prefab";
        if (!prefab.TryGetComponent<Piece>(out var piece) || !prefab.TryGetComponent<WearNTear>(out var wear)) return "not a wear piece";
        string nativeReason = WeatherGuardRules.Exclusion(zdo.GetLong(ZDOVars.s_creator), zdo.Persistent, wear.m_staticPosition,
                wear.m_noRoofWear, zdo.GetFloat(ZDOVars.s_health, wear.m_health),
                piece.m_category == Piece.PieceCategory.BuildingWorkbench || piece.m_category == Piece.PieceCategory.BuildingStonecutter || piece.m_category == Piece.PieceCategory.DeepNorth,
                prefab.GetComponent<Container>() != null);
        return WeatherGuardRules.SelectionReason(nativeReason, prefab.name, _excludedPrefabs, _protectRain, _protectUnderwater);
    }
    private Dictionary<string, object?> ContextBlocker(ZDO zdo, ZNetScene scene, ZoneSystem zones)
    {
        var sector = zdo.GetSector();
        for (int x = sector.x - 1; x <= sector.x + 1; x++)
        for (int y = sector.y - 1; y <= sector.y + 1; y++)
            if (!zones.IsZoneLoaded(new Vector2s(x, y))) return new() { ["missing_terrain_sector"] = $"{x},{y}" };
        var objects = new List<ZDO>();
        _manager.FindSectorObjects(sector, new SimulationDistance(1, 0), objects);
        foreach (var candidate in objects)
            if (scene.GetPrefab(candidate.GetPrefab()) is GameObject prefab && RequiresContext(candidate, scene) && scene.FindInstance(candidate) == null)
                return new() { ["missing_object"] = candidate.m_uid.ToString(), ["missing_prefab"] = prefab.name,
                    ["missing_position"] = Identity(candidate)["position"], ["missing_persistent"] = candidate.Persistent,
                    ["prefab_colliders"] = prefab.GetComponentsInChildren<Collider>(true).Length,
                    ["prefab_rigidbodies"] = prefab.GetComponentsInChildren<Rigidbody>(true).Length,
                    ["prefab_wear_piece"] = prefab.GetComponent<WearNTear>() != null,
                    ["retained_near"] = _nearIds.Contains(candidate.m_uid), ["retained_distant"] = _distantIds.Contains(candidate.m_uid) };
        return new() { ["missing_dependency"] = "not_found_in_sample" };
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
        ready = TerrainReady(zdo, zones);
        if (ready)
        {
            _contextObjects.Clear();
            _manager.FindSectorObjects(sector, new SimulationDistance(1, 0), _contextObjects);
            foreach (var candidate in _contextObjects)
                if (RequiresContext(candidate, scene) && scene.FindInstance(candidate) == null) { ready = false; break; }
        }
        _readyContexts.Add(sector, ready);
        return ready;
    }
    private bool RequiresContext(ZDO zdo, ZNetScene scene)
    {
        int id = zdo.GetPrefab();
        if (_contextPrefabs.TryGetValue(id, out bool required)) return required;
        var prefab = scene.GetPrefab(id);
        if (prefab == null) return false;
        // Roof/support physics need collider-bearing objects. Do not wait for
        // collider-free sounds/VFX, but retain wear and shield dependencies.
        required = WeatherGuardRules.RequiresContext(prefab.GetComponentsInChildren<Collider>(true).Length,
            prefab.GetComponent<WearNTear>() != null, prefab.GetComponent<ShieldGenerator>() != null);
        _contextPrefabs.Add(id, required);
        return required;
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
        bool succeeded = true;
        try { succeeded = ModuleCleanup.Run(_log, _module.Name, _patches.Dispose); }
        finally
        {
            // With guards inactive, let vanilla choose a new owner even if unpatching failed.
            try
            {
                Exception? firstError = null;
                int failures = 0;
                // Release through the captured world, never a replacement world's manager.
                foreach (var zdo in _protected.Values)
                {
                    try
                    {
                        if (!zdo.IsValid() || !ReferenceEquals(_manager, ZDOMan.instance) || zdo.GetOwner() != ServerId) continue;
                        zdo.SetOwner(0L);
                        _manager.ForceSendZDO(zdo.m_uid);
                    }
                    catch (Exception error) { succeeded = false; firstError ??= error; failures++; }
                }
                if (firstError != null) DiagnosticLog.Write(_log, _module.Name, DiagnosticEvent.Lifecycle,
                    "Ownership release failed for some pieces; remaining releases attempted: " + firstError,
                    new (string, object?)[] { ("release_failures", failures) }, LogLevel.Error);
            }
            finally { _protected.Clear(); _positions.Clear(); _near.Clear(); _distant.Clear(); _rainEvidence.Clear(); _observedCandidates.Clear(); _contextPrefabs.Clear(); _candidateReasons.Clear(); }
        }
        if (!succeeded) throw new InvalidOperationException("Controller cleanup did not complete; paused state cannot be confirmed.");
    }
}
