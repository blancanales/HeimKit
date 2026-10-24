using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Features.InfiniteTorches.Models;
using System;
using System.Collections.Generic;
using BepInEx.Logging;


namespace HeimKit.Features.InfiniteTorches;

internal sealed class InfiniteTorchesController : IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly IPlayerPresence _presence;
    private readonly InfiniteTorches _module;
    // No custom RPCs, client patches, ownership changes or direct ZDO writes.
    private readonly InfiniteTorchesSettings _settings;
    private readonly InfiniteTorchesDiagnostics _diagnostics;
    private readonly InfiniteTorchesPendingRefills _pending = new();
    private readonly PrefabCatalog<Fireplace> _torches = PrefabRegistry.Current.Catalog<Fireplace>("InfiniteTorchesController._torches");
    private readonly List<ZDO> _sector = new();
    private CandidateSubscription? _index;
    private readonly List<ZDO> _distant = new();
    private readonly List<ZDO> _candidates = new();
    private readonly HashSet<ZDOID> _seen = new();
    private readonly Dictionary<long, ZNetPeer> _owners = new();
    private ZNetScene? _scene;
    private string _configuredNames = "";
    private int _configuredFireplaces;
    private int _cursor;
    private float _reconcileNow;
    private readonly Func<ZDOID, RefillObservation?> _observeRefill;
    private readonly Action<RefillResult> _reportRefill;
    private bool _announced;
    private readonly Dictionary<string, long> _outcomes = new();

    public InfiniteTorchesController(InfiniteTorches module, InfiniteTorchesSettings settings, ManualLogSource logger, IPlayerPresence presence)
    {
        _logger = logger;
        _observeRefill = ObserveRefill;
        _presence = presence;
        _module = module;
        _settings = settings;
        _diagnostics = new InfiniteTorchesDiagnostics(() => _settings.DebugLogging.Enabled, logger, () => new Dictionary<string, long>(_outcomes));
        _reportRefill = result => { Count(result.Outcome); _diagnostics.Result(_reconcileNow, result); };
    }

    internal string SettingsDescription => _diagnostics.SettingsDescription;

    public void Tick(float now)
    {
        if (!ModuleScheduler.Current.Due(this, "processing", now, _settings.IntervalSeconds.Value)) return;
        var net = ZNet.instance;
        var scene = ZNetScene.instance;
        var manager = ZDOMan.instance;
        var routed = ZRoutedRpc.instance;
        if (!WorldServices.World) { ReportUnavailableWorld(now); return; }

        string operation = "resolving configured light-source prefabs";
        ZNetPeer? scanningPeer = null;
        try
        {
            operation = "checking pending refill results";
            _reconcileNow = now;
            int fuelUpdates = _pending.Reconcile(now, _observeRefill, _reportRefill);
            _diagnostics.RefillSummary(fuelUpdates);
            operation = "resolving configured light-source prefabs";
            if (_scene != scene || _configuredNames != _settings.Prefabs.Value || _configuredFireplaces != _settings.FireplaceSelection) LoadPrefabs(scene);
            if (_torches.Count == 0)
            {
                if (_diagnostics.SummaryReady(now)) _diagnostics.Report(now, () => "No supported light-source prefabs configured. Check Torches.Prefabs and startup warnings.");
                return;
            }
            _owners.Clear();
            for (int rosterIndex1 = 0; rosterIndex1 < _presence.Peers.Count && _presence.Peers[rosterIndex1] is var peer; rosterIndex1++)
                if (peer.IsReady() && !peer.m_characterID.IsNone()) _owners[peer.m_uid] = peer;

            _candidates.Clear();
            _seen.Clear();
            if (_owners.Count == 0)
            {
                if (_diagnostics.SummaryReady(now)) _diagnostics.Report(now, () => "No ready connected players with character IDs.");
                return;
            }
            int sectorRecords = 0;
            // The dedicated server need not instantiate a Fireplace. Look up ZDOs in each owner's active sectors.
            foreach (var peer in _owners.Values)
            {
                operation = "scanning sectors"; scanningPeer = peer;
                _sector.Clear();
                _distant.Clear();
                _index!.Read(peer.m_refPos, peer.m_simulationDistance.NearSimulationDistance, _sector, peer.m_simulationDistance.IsClassic, includeDistant: false);
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
                if (_diagnostics.SummaryReady(now)) ReportEmptyScope(now, sectorRecords);
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
                Count("requests_sent");
                if (_diagnostics.IsDebugEnabled) _diagnostics.Request(now, zdo.m_uid.ToString(), prefab.gameObject.name, position, fuel, prefab.m_maxFuel);
            }
            if (_diagnostics.SummaryReady(now)) ReportScan(now, sent, examined, sufficientFuel, stateOff, invalidFuel, changedOwner, pendingSkipped, trackingFull);
        }
        catch (Exception ex)
        {
            if (operation == "scanning sectors" && scanningPeer != null) operation = $"scanning sectors for owner {scanningPeer.m_uid} at {scanningPeer.m_refPos}";
            _module.Fail(ex, operation);
        }
    }

    private void ReportUnavailableWorld(float now)
    {
        if (!_diagnostics.SummaryReady(now)) return;
        var net = ZNet.instance; var scene = ZNetScene.instance;
        var manager = ZDOMan.instance; var routed = ZRoutedRpc.instance;
        if (net == null || scene == null || manager == null || routed == null)
            _diagnostics.Report(now, () => "Waiting for world services. Unavailable: " +
                $"{(net == null ? "network " : "")}{(scene == null ? "scene " : "")}" +
                $"{(manager == null ? "world-object data " : "")}{(routed == null ? "RPC routing" : "")}".TrimEnd() + ".");
        else _diagnostics.Report(now, () => "Not running in a dedicated-server network session.");
    }
    private void ReportEmptyScope(float now, int sectorRecords) => _diagnostics.Report(now, () =>
        $"No eligible client-owned light sources in players' nearby areas; ready_players_now={_owners.Count}; prefab_types_now={_torches.Count}; sector_records_pass={sectorRecords}.");
    private void ReportScan(float now, int sent, int examined, int sufficientFuel, int stateOff, int invalidFuel, int changedOwner, int pendingSkipped, int trackingFull)
    {
            if (_diagnostics.SummaryReady(now)) _diagnostics.Report(now, () =>
            {
                string reason = sent > 0 ? "Refill requests sent this pass; client application is unconfirmed."
                    : sufficientFuel == examined ? "All examined light sources have enough fuel (at least 75%)."
                    : pendingSkipped > 0 ? "Waiting for previously requested refills; duplicate requests suppressed."
                    : "No examined light source could be refilled this pass; see skip counts.";
                return $"{reason}; ready_players_now={_owners.Count}; candidates_now={_candidates.Count}; examined_pass={examined}; requests_sent_pass={sent}; " +
                    $"sufficient_fuel_pass={sufficientFuel}; off_pass={stateOff}; invalid_fuel_pass={invalidFuel}; owner_unavailable_pass={changedOwner}; " +
                    $"pending_now={_pending.Count}; duplicates_suppressed_pass={pendingSkipped}; tracking_capacity_skips_pass={trackingFull}; " +
                    $"deferred_pass={_candidates.Count - examined}.";
            });
    }
    private RefillObservation? ObserveRefill(ZDOID id)
    {
        var data = ZDOMan.instance.GetZDO(id);
        return data != null && data.IsValid() ? new RefillObservation(data.GetOwner(), data.GetFloat(ZDOVars.s_fuel, -1)) : (RefillObservation?)null;
    }
    private void Count(string kind) { _outcomes.TryGetValue(kind, out long count); _outcomes[kind] = count + 1; }

    private void LoadPrefabs(ZNetScene scene)
    {
        _index?.Dispose(); _index = null;
        _torches.Clear();
        var rejected = new Dictionary<string, int>();
        int rejectionCount = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in _settings.Prefabs.Value.Split(','))
            if (raw.Trim().Length > 0) names.Add(raw.Trim());
        foreach (string fireplace in new[] { "fire_pit", "hearth", "bonfire" })
            if (_settings.FireplaceEnabled(fireplace)) names.Add(fireplace);
        foreach (string raw in names)
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            var go = PrefabRegistry.Current.Prefab(name);
            var fire = go != null ? PrefabRegistry.Component<Fireplace>(go) : null;
            string? rejection = go == null ? "prefab not found" : fire == null ? "no Fireplace component"
                : InfiniteTorchesRefillRules.RejectionReason(name, fire.m_canRefill, fire.m_infiniteFuel,
                    fire.m_canTurnOff, fire.m_enabledObjectLow != null || fire.m_enabledObjectHigh != null,
                    fire.m_maxFuel, _settings.FireplaceEnabled(name));
            if (rejection != null)
            {
                rejected.TryGetValue(rejection, out int count); rejected[rejection] = count + 1; rejectionCount++;
                continue;
            }
            _torches[scene.GetPrefabHash(go!)] = fire!;
        }
        _diagnostics.UnsupportedPrefabs(rejectionCount, rejected);
        _index = WorldObjectProvider.Current.Subscribe("InfiniteTorches", z => _torches.ContainsKey(z.GetPrefab()));
        _scene = scene;
        _configuredNames = _settings.Prefabs.Value;
        _configuredFireplaces = _settings.FireplaceSelection;
        if (!_announced)
        {
            if (_torches.Count > 0) _module.ProcessingReady();
            else DiagnosticLog.Write(_logger, "InfiniteTorches", "No supported light-source prefabs are available; refilling is unavailable.", level: LogLevel.Warning);
            if (_diagnostics.IsDebugEnabled)
                if (DiagnosticLog.DebugAvailable(_logger, "InfiniteTorches")) DiagnosticLog.Write(_logger, "InfiniteTorches", InfiniteTorchesLogTopics.Refill, "Resolved light-source prefab definitions.", new (string, object?)[] { ("prefab_types", _torches.Count) }, LogLevel.Debug);
            _announced = true;
        }
    }
    public void Dispose()
    {
        _index?.Dispose(); _index = null;
        _torches.Clear();
        _sector.Clear();
        _distant.Clear();
        _candidates.Clear();
        _seen.Clear();
        _owners.Clear();
        _scene = null;
        _configuredNames = "";
        _cursor = 0;
        _announced = false;
        _diagnostics.Reset();
        _pending.Clear();
    }
}
