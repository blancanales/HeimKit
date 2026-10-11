using HeimKit.Features.InfiniteTorches.Models;
using System;
using System.Collections.Generic;
using BepInEx.Logging;


namespace HeimKit.Features.InfiniteTorches;

internal sealed class InfiniteTorchesController : IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly InfiniteTorches _module;
    // No custom RPCs, client patches, ownership changes or direct ZDO writes.
    private readonly InfiniteTorchesSettings _settings;
    private readonly InfiniteTorchesDiagnostics _diagnostics;
    private readonly InfiniteTorchesPendingRefills _pending = new();
    private readonly Dictionary<int, Fireplace> _torches = new();
    private readonly List<ZDO> _sector = new();
    private readonly List<ZDO> _distant = new();
    private readonly List<ZDO> _candidates = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly Dictionary<long, ZNetPeer> _owners = new();
    private ZNetScene? _scene;
    private string _configuredNames = "";
    private float _nextTick;
    private int _cursor;
    private bool _announced;

    public InfiniteTorchesController(InfiniteTorches module, InfiniteTorchesSettings settings, ManualLogSource logger)
    {
        _logger = logger;
        _module = module;
        _settings = settings;
        _diagnostics = new InfiniteTorchesDiagnostics(_settings.DebugMode, logger);
    }

    internal string SettingsDescription => _diagnostics.SettingsDescription;

    public void Tick(float now)
    {
        if (now < _nextTick) return;
        _nextTick = now + _settings.IntervalSeconds.Value;
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        var manager = ZDOMan.instance;
        var routed = ZRoutedRpc.instance;
        if (net == null || scene == null || manager == null || routed == null)
        {
            _diagnostics.Report(now, () => "Waiting for world services. Unavailable: " +
                $"{(net == null ? "network " : "")}{(scene == null ? "scene " : "")}" +
                $"{(manager == null ? "world-object data " : "")}{(routed == null ? "RPC routing" : "")}".TrimEnd() + ". No requests sent this pass.");
            return;
        }
        if (!net.IsServer() || !net.IsDedicated())
        {
            _diagnostics.Report(now, () => "Not running in a dedicated-server network session. No requests sent this pass.");
            return;
        }

        string operation = "resolving configured torch prefabs";
        try
        {
            operation = "checking pending refill results";
            _pending.Reconcile(now, id =>
            {
                var objectData = manager.GetZDO(id);
                return objectData != null && objectData.IsValid()
                    ? new RefillObservation(objectData.GetOwner(), objectData.GetFloat(ZDOVars.s_fuel, -1f))
                    : (RefillObservation?)null;
            }, _diagnostics.IsDebugEnabled ? message => _logger.LogInfo(message) : (Action<string>?)null);
            operation = "resolving configured torch prefabs";
            if (_scene != scene || _configuredNames != _settings.Prefabs.Value) LoadPrefabs(scene);
            if (_torches.Count == 0)
            {
                _diagnostics.Report(now, () => "No supported torch prefabs configured. Check Torches.Prefabs and startup warnings. No requests sent this pass.");
                return;
            }
            _owners.Clear();
            foreach (var peer in net.GetPeers())
                if (peer.IsReady() && !peer.m_characterID.IsNone()) _owners[peer.m_uid] = peer;

            _candidates.Clear();
            _seen.Clear();
            if (_owners.Count == 0)
            {
                _diagnostics.Report(now, () => "No ready connected players with character IDs. No requests sent this pass.");
                return;
            }
            int sectorRecords = 0;
            // The dedicated server need not instantiate a Fireplace. Look up ZDOs in each owner's active sectors.
            foreach (var peer in _owners.Values)
            {
                operation = $"scanning sectors for owner {peer.m_uid} at {peer.m_refPos}";
                _sector.Clear();
                _distant.Clear();
                manager.FindSectorObjects(ZoneSystem.GetZone(peer.m_refPos), peer.m_simulationDistance, _sector, _distant);
                sectorRecords += _sector.Count;
                foreach (var zdo in _sector)
                {
                    if (zdo.IsValid() && zdo.GetOwner() == peer.m_uid &&
                        _torches.ContainsKey(zdo.GetPrefab()) && _seen.Add(zdo.m_uid))
                        _candidates.Add(zdo);
                }
            }
            if (_candidates.Count == 0)
            {
                _cursor = 0;
                _diagnostics.Report(now, () => $"No eligible client-owned torches in players' nearby areas. Ready players={_owners.Count}; prefab types={_torches.Count}; sector records examined={sectorRecords}. No requests sent this pass.");
                return;
            }

            int sent = 0;
            int examined = 0;
            int sufficientFuel = 0;
            int stateOff = 0;
            int invalidFuel = 0;
            int changedOwner = 0;
            int pendingSkipped = 0;
            int trackingFull = 0;
            _cursor %= _candidates.Count;
            while (examined < _candidates.Count && sent < _settings.MaxMessagesPerPass.Value)
            {
                var zdo = _candidates[_cursor];
                _cursor = (_cursor + 1) % _candidates.Count;
                examined++;
                var prefab = _torches[zdo.GetPrefab()];
                long owner = zdo.GetOwner();
                if (!_owners.ContainsKey(owner)) { changedOwner++; continue; }
                if (zdo.GetInt(ZDOVars.s_state, 1) != 1) { stateOff++; continue; }
                float fuel = zdo.GetFloat(ZDOVars.s_fuel, prefab.m_startFuel);
                if (float.IsNaN(fuel) || float.IsInfinity(fuel)) { invalidFuel++; continue; }
                // Refill below 75%, avoiding constant RPC effects and leaving ample burn time for slow passes.
                if (fuel >= prefab.m_maxFuel * 0.75f) { sufficientFuel++; continue; }
                if (_pending.Contains(zdo.m_uid)) { pendingSkipped++; continue; }
                var position = zdo.GetPosition();
                operation = $"requesting refill: object={zdo.m_uid}; prefab={prefab.gameObject.name}; owner={owner}; position={position}; fuel={fuel}; target={prefab.m_maxFuel}";
                if (!_pending.TryTrack(zdo.m_uid, owner, fuel, prefab.m_maxFuel, now)) { trackingFull++; continue; }
                try { routed.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_SetFuelAmount", prefab.m_maxFuel); }
                catch { _pending.Remove(zdo.m_uid); throw; }
                sent++;
                _diagnostics.LogRefillRequest(now, () => $"object={zdo.m_uid}; prefab={prefab.gameObject.name}; owner={owner}; position={position}; fuel before={fuel:F3}; target={prefab.m_maxFuel:F3}; RPC=RPC_SetFuelAmount. Sent to routing; client application unconfirmed.");
            }
            if (_diagnostics.IsDebugEnabled && sent > 0)
                _logger.LogInfo($"Sent {sent} refill requests; {_candidates.Count} eligible owner-area torches. Client application is unconfirmed.");
            _diagnostics.Report(now, () =>
            {
                string reason = sent > 0 ? "Refill requests sent this pass; client application is unconfirmed."
                    : sufficientFuel == examined ? "All examined torches have enough fuel (at least 75%). No requests sent this pass."
                    : pendingSkipped > 0 ? "Waiting for previously requested refills; duplicate requests suppressed."
                    : "No examined torch could be refilled this pass; see skip counts.";
                return $"{reason} Ready players={_owners.Count}; candidates={_candidates.Count}; examined={examined}; sent={sent}; " +
                    $"sufficient fuel={sufficientFuel}; off={stateOff}; invalid fuel={invalidFuel}; owner unavailable={changedOwner}; " +
                    $"pending={_pending.Count}; duplicate requests suppressed={pendingSkipped}; tracking capacity skips={trackingFull}; " +
                    $"deferred={_candidates.Count - examined}.";
            });
        }
        catch (Exception ex)
        {
            _module.Fail(ex, operation);
        }
    }

    private void LoadPrefabs(ZNetScene scene)
    {
        _torches.Clear();
        foreach (string raw in _settings.Prefabs.Value.Split(','))
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            var go = scene.GetPrefab(name);
            var fire = go != null ? go.GetComponent<Fireplace>() : null;
            string? rejection = go == null ? "prefab not found" : fire == null ? "no Fireplace component"
                : !fire.m_canRefill ? "refilling not supported" : fire.m_infiniteFuel ? "already has infinite fuel"
                : fire.m_canTurnOff ? "toggle-off behavior is outside the supported torch scope"
                : fire.m_enabledObjectLow != null || fire.m_enabledObjectHigh != null ? "wet-flame behavior is outside the supported torch scope"
                : fire.m_maxFuel <= 0f || float.IsNaN(fire.m_maxFuel) || float.IsInfinity(fire.m_maxFuel) ? "invalid maximum fuel" : null;
            if (rejection != null)
            {
                _logger.LogWarning($"Infinite Torches skipped prefab '{name}': {rejection}.");
                continue;
            }
            _torches[scene.GetPrefabHash(go!)] = fire!;
        }
        _scene = scene;
        _configuredNames = _settings.Prefabs.Value;
        if (!_announced)
        {
            _logger.LogInfo($"Infinite Torches: ready with {_torches.Count} prefabs. Vanilla owners receive vanilla fuel RPCs. Unloaded or transferring objects retry on later passes.");
            _announced = true;
        }
    }
    public void Dispose()
    {
        _torches.Clear();
        _sector.Clear();
        _distant.Clear();
        _candidates.Clear();
        _seen.Clear();
        _owners.Clear();
        _scene = null;
        _configuredNames = "";
        _nextTick = 0f;
        _cursor = 0;
        _announced = false;
        _diagnostics.Reset();
        _pending.Clear();
    }
}
