using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.StationSupply.Models;
using UnityEngine;
namespace HeimKit.Features.StationSupply;

internal sealed class StationSupplyController : IDisposable
{
    private readonly StationSupplySettings _settings;
    private readonly ManualLogSource _log;
    private readonly PlayerServices _players;
    private readonly PlayerWorldScope _scope;
    private readonly StationSupplyPrefabs _prefabs;
    private readonly StationSupplyDiagnostics _diagnostics;
    private readonly ObjectTransferAuthority _authority;
    private readonly StructureContext? _context;
    private readonly StationSupplyObliterator? _obliterator;
    private readonly Func<int, bool>? _disposalItem;
    private readonly List<ZDO> _stations = new(), _chestRecords = new(), _wards = new();
    private readonly List<SupplyChest> _chests = new();
    private bool _chestsPrepared;
    private readonly StationSupplyInputHistory _inputHistory = new();
    private readonly StationSupplyIntakeOrder _intakeOrder = new();
    private readonly StationSupplyManualAccess _manual = new();
    private readonly StationSupplyRetry _retry = new();
    private readonly SupplyCounts _counts = new();
    private readonly Func<int> _timingScope;
    private IDisposable? _messages;
    private readonly List<SupplyTransaction> _active = new(4);
    private SupplyTransaction? _currentTransaction;
    private int _cursor;
    private bool _disposed;
    private static readonly int Pong = "RPC_Pong".GetStableHashCode();
    private static class FermenterAccess
    {
        internal static readonly Action<Fermenter, float, bool> Cover = NativeAccess.Method<Action<Fermenter, float, bool>>(typeof(Fermenter), "UpdateCover", typeof(float), typeof(bool));
        internal static readonly HarmonyLib.AccessTools.FieldRef<Fermenter, bool> Roof = NativeAccess.Field<Fermenter, bool>("m_hasRoof");
        internal static readonly HarmonyLib.AccessTools.FieldRef<Fermenter, bool> Exposed = NativeAccess.Field<Fermenter, bool>("m_exposed");
    }
    internal StationSupplyController(StationSupply module, StationSupplySettings settings, ManualLogSource log, PlayerServices players)
    {
        _settings = settings; _log = log; _players = players;
        _timingScope = () => _stations.Count + _chestRecords.Count + _wards.Count;
        NativeInventory.CheckCompatibility();
        _prefabs = new(settings); _scope = WorldObjectProvider.Current.CreateScope("StationSupply");
        _diagnostics = new(log, settings);
        if (_prefabs.Stations.TryGetValue("incinerator".GetStableHashCode(), out var disposal))
        {
            _obliterator = new(Time.unscaledTime, disposal.Intakes[0].QuietSeconds);
            var categories = new StationSupplyItemCategories();
            _disposalItem = prefab => categories.Allowed(prefab, settings.ObliteratorCategories.Value);
        }
        ObjectTransferAuthority? authority = null;
        StructureContext? context = null;
        IDisposable? messages = null;
        try
        {
            authority = new(players.Presence, module.Fail, "StationSupply", staticObjects: true, containerRequestStatus: ManualRequest);
            if (settings.Fermenters.Value) { _ = FermenterAccess.Cover; context = new(); }
            _scope.Index(_prefabs.Accept);
            _authority = authority; _context = context;
            messages = players.RoutedMessages.Subscribe(Observe, module.Fail); _messages = messages;
            _diagnostics.Configuration(Time.unscaledTime, _prefabs);
        }
        catch
        {
            ModuleCleanup.Run(log, "StationSupply", () => messages?.Dispose(), () => authority?.Dispose(), () => _scope.Dispose(), () => context?.Dispose());
            throw;
        }
    }
    public void Tick(float now)
    {
        if (_disposed) return;
        _diagnostics.FlushTransfers(now);
        _authority.TickContainerRequests(now);
        if (EnvMan.instance.IsTimeSkipping())
        {
            if (_active.Count != 0) EndAll(now, "sleep_skip");
            else if (ModuleScheduler.Current.Due(this, "sleep_notice", now, _settings.IntervalSeconds.Value)) Evidence(now, "sleep_skip");
            return;
        }
        if (_active.Count != 0)
        {
            if (!ModuleScheduler.Current.Due(this, "transaction", now, .2f)) return;
            using var pendingTiming = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, _timingScope) : default;
            AdvanceSet(now, 0);
            if (_settings.MaxActiveTransactions.Value == 1) return;
        }
        if (_active.Count >= _settings.MaxActiveTransactions.Value) return;
        if (!ModuleScheduler.Current.Due(this, "discovery", now, _settings.IntervalSeconds.Value, cooperative: true)) return;
        using var discoveryTiming = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, _timingScope) : default;
        using (_diagnostics.Stage("world_discovery"))
        {
            if (!_scope.Refresh(_settings.ScanRangeMeters.Value, outdoorOnly: true)) { Evidence(now, _scope.UnavailableReason ?? "player_scope_unavailable"); return; }
            PrepareSelections();
        }
        Evidence(now, "player_scope_ready");
        _diagnostics.Scope(now, _stations.Count, _chestRecords.Count, _counts);
        int count = _stations.Count;
        int budget = Math.Min(count, _settings.MaxStationsPerPass.Value);
        int firstNew = _active.Count;
        for (int visited = 0; visited < budget && _active.Count < _settings.MaxActiveTransactions.Value; visited++)
        {
            int index = _cursor++ % count; if (_cursor >= 1000000) _cursor = 0;
            var station = _stations[index];
            if (!station.IsValid() || !_prefabs.Stations.TryGetValue(station.GetPrefab(), out var definition)) continue;
            if (!Disjoint(_active, station.m_uid, station.m_uid)) { Evidence(now, "record_in_active_transaction", station); continue; }
            string? exclusion = station.GetLong(ZDOVars.s_creator) == 0 ? "station_not_player_built" :
                !StationSupplyRules.NativeScale(station) ? "station_non_native_scale" :
                !_scope.InRange(station.GetPosition(), _settings.ScanRangeMeters.Value) ? "station_outside_player_range" :
                Character.InInterior(station.GetPosition()) ? "station_in_interior" : !NativeCharacterHealth.IsAlive(station) ? "station_not_alive" : null;
            if (exclusion != null) { Evidence(now, exclusion, station); continue; }
            int start = _intakeOrder.Start(station, definition.Intakes.Length);
            for (int i = 0; i < definition.Intakes.Length; i++)
            {
                var port = definition.Intakes[(start + i) % definition.Intakes.Length];
                string? notReady = IntakeWait(station, port, now, out float remaining);
                if (notReady != null) { Evidence(now, notReady, station, port, remaining: remaining); continue; }
                var chest = Select(station, port, now, out var selection);
                if (chest == null) { Evidence(now, "no_chest_at_intake", station, port, selection: selection); continue; }
                if (!Disjoint(_active, station.m_uid, chest.m_uid)) { Evidence(now, "record_in_active_transaction", station, port, chest, selection); continue; }
                if (Open(chest)) { _manual.Observe(chest.m_uid, now); Evidence(now, "selected_chest_open", station, port, chest, selection); continue; }
                if (!_manual.Available(chest.m_uid, now, out float grace))
                { Evidence(now, "selected_chest_manual_grace", station, port, chest, selection, remaining: grace); continue; }
                if (!Access(station, chest)) { Evidence(now, "ward_access_denied", station, port, chest, selection); continue; }
                var bytes = chest.GetByteArray(ZDOVars.s_items);
                if (bytes == null || bytes.Length <= 6)
                {
                    string reason = bytes == null ? "source_inventory_unavailable" : bytes.Length == 6 && bytes[0] == NativeInventory.Format && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 && bytes[4] == 0 && bytes[5] == 0 ? "selected_chest_empty" : "invalid_source_inventory";
                    if (StationSupplyDiagnostics.Actionable(reason))
                    {
                        _counts.Failures++;
                        using (_diagnostics.Stage("diagnostics")) _diagnostics.PreflightIncident(now, definition.Name, station.m_uid, port.Kind, chest.m_uid, reason);
                    }
                    Evidence(now, reason, station, port, chest, selection); continue;
                }
                if (_inputHistory.TryGet(chest, port, out var rejected))
                { Evidence(now, rejected.Status, station, port, chest, selection, inventory: rejected); continue; }
                bool stationReady = _authority.Preflight(station, out string stationWait);
                bool chestReady = _authority.Preflight(chest, out string chestWait);
                var retryState = RetryState(station, chest, stationReady, chestReady);
                if (!_retry.Ready(station.m_uid, port, chest.m_uid, retryState, now, out float retryIn))
                { ServerPerformance.RetryDeferrals.Add(0); Evidence(now, "ownership_retry_backoff", station, port, chest, selection, remaining: retryIn); continue; }
                if (!stationReady || !chestReady)
                {
                    _retry.Failed(station.m_uid, port, chest.m_uid, retryState, now);
                    Evidence(now, !stationReady ? "station_ownership_unavailable" : "chest_ownership_unavailable", station, port, chest, selection,
                        detail: !stationReady ? stationWait : chestWait); continue;
                }
                var transaction = new SupplyTransaction { Station = new(station), Chest = new(chest), Definition = definition, Intake = port,
                    StationPosition = station.GetPosition(), StationRotation = station.GetRotation(), ChestPosition = chest.GetPosition(), Started = now, Selection = selection, Stage = "ownership_acquisition" };
                transaction.Chest.ManualContainerAccess = true;
                transaction.Station.ManualContainerAccess = port.Kind == SupplyKind.Obliterator;
                _active.Add(transaction); _currentTransaction = transaction;
                _counts.Transactions++;
                using (_diagnostics.Stage("ownership"))
                {
                    if (!_authority.Acquire(transaction.Station, now, out var stationReason)) { transaction.StationWait = stationReason; End(transaction, now, "station_ownership_unavailable"); if (_settings.MaxActiveTransactions.Value == 1) return; break; }
                    if (transaction.Station.PreviousPeer != null) _counts.SourceDrains++;
                    if (!_authority.Acquire(transaction.Chest, now, out var chestReason)) { transaction.ChestWait = chestReason; End(transaction, now, "chest_ownership_unavailable"); if (_settings.MaxActiveTransactions.Value == 1) return; break; }
                    if (transaction.Chest.PreviousPeer != null) _counts.SourceDrains++;
                }
                transaction.Stage = "source_state_drain";
                _currentTransaction = null;
                break;
            }
        }
        AdvanceSet(now, firstNew);
    }
    internal static bool Disjoint(IReadOnlyList<SupplyTransaction> active, ZDOID station, ZDOID chest)
    {
        for (int i = 0; i < active.Count; i++)
        {
            var t = active[i];
            if (t.Station.Id == station || t.Station.Id == chest || t.Chest.Id == station || t.Chest.Id == chest) return false;
        }
        return true;
    }
    private void AdvanceSet(float now, int start)
    {
        for (int i = start; i < _active.Count;)
        {
            var t = _active[i]; _currentTransaction = t;
            Advance(t, now);
            _currentTransaction = null;
            if (i < _active.Count && ReferenceEquals(_active[i], t)) i++;
        }
    }
    private void PrepareSelections()
    {
        _stations.Clear(); _chestRecords.Clear(); _chests.Clear(); _wards.Clear(); _chestsPrepared = false;
        foreach (var record in _scope.Records)
        {
            int prefab = record.GetPrefab();
            if (_prefabs.Chests.ContainsKey(prefab)) _chestRecords.Add(record);
            else if (_prefabs.Stations.ContainsKey(prefab)) _stations.Add(record);
            else if (_prefabs.Wards.ContainsKey(prefab)) _wards.Add(record);
        }
    }
    private void PrepareChests()
    {
        if (_chestsPrepared) return;
        foreach (var record in _chestRecords)
        {
            if (!record.IsValid()) continue;
            string? rejection = record.GetLong(ZDOVars.s_creator) == 0 ? "chest_not_player_built" :
                !NativeCharacterHealth.IsAlive(record) ? "chest_not_alive" : !StationSupplyRules.NativeScale(record) ? "chest_non_native_scale" : null;
            _chests.Add(new SupplyChest(record, rejection));
        }
        _chestsPrepared = true;
    }
    private ZDO? Select(ZDO station, SupplyIntake intake, float now, out SupplySelection selection)
    {
        using var measured = _diagnostics.Stage("chest_selection");
        selection = new SupplySelection { Checked = true };
        PrepareChests();
        ZDO? best = null; float distance = float.MaxValue;
        bool diagnostics = _diagnostics.SelectionReady(now);
        selection.CountsMeasured = diagnostics;
        float rejectedDistance = float.MaxValue;
        var placement = StationSupplyRules.Placement(station.GetPosition(), station.GetRotation(), intake.LocalPoint, _settings.ChestRangeMeters.Value, _settings.ChestHeightToleranceMeters.Value);
        foreach (var candidateChest in _chests)
        {
            var chest = candidateChest.Record;
            string? rejection = candidateChest.Rejection;
            float? score = null;
            if (rejection == null) score = StationSupplyRules.Distance(placement, candidateChest.Position, out rejection);
            if (diagnostics)
            {
                if (score.HasValue) selection.Eligible++;
                else
                {
                    if (rejection == "chest_height_exceeded") selection.WrongHeight++;
                    else if (rejection == "chest_on_inward_side") selection.WrongSide++;
                    else if (rejection == "chest_range_exceeded") selection.OutOfRange++;
                    else selection.Ineligible++;
                    var offset = candidateChest.Position - placement.Station - placement.Anchor; offset.y = 0;
                    float candidate = offset.sqrMagnitude;
                    if (PlayerWorldScope.Finite(candidate) && (selection.NearestRejected == null || StationSupplyRules.Prefer(candidate, chest.m_uid, rejectedDistance, selection.NearestRejected.m_uid)))
                    { selection.NearestRejected = chest; selection.Rejection = rejection; rejectedDistance = candidate; }
                }
            }
            if (score.HasValue && (best == null || StationSupplyRules.Prefer(score.Value, chest.m_uid, distance, best.m_uid))) { best = chest; distance = score.Value; }
        }
        selection.Selected = best; return best;
    }
    private bool Access(ZDO station, ZDO chest)
    {
        using var measured = _diagnostics.Stage("native_context");
        return StationSupplyAccess.Allowed(_wards, _prefabs.Wards, station.GetLong(ZDOVars.s_creator), station.GetPosition(), chest.GetPosition());
    }
    private static bool Open(ZDO chest)
    {
        if (chest.GetInt(ZDOVars.s_inUse) != 0) return true;
        var instance = ZNetScene.instance.FindInstance(chest); var container = instance == null ? null : instance.GetComponent<Container>();
        if (container == null && instance != null) container = instance.GetComponent<Incinerator>()?.m_container;
        return container != null && container.IsInUse();
    }
    private string? IntakeWait(ZDO station, SupplyIntake intake, float now, out float remaining)
    {
        remaining = 0;
        if (!Room(station, intake))
        {
            if (intake.Kind == SupplyKind.Obliterator) _manual.Observe(station.m_uid, now);
            return intake.Kind == SupplyKind.Obliterator ? "obliterator_open" : "station_has_no_room";
        }
        if (intake.Kind != SupplyKind.Obliterator) return null;
        if (!_manual.Available(station.m_uid, now, out remaining)) return "obliterator_manual_grace";
        if (_settings.ObliteratorCategories.Value == ObliteratorCategories.None) return "obliterator_no_categories_selected";
        if (_obliterator == null) return "obliterator_observer_unavailable";
        if (!_obliterator.Quiet(station.m_uid, now, out string reason, out remaining)) return reason;
        if (Open(station)) { _manual.Observe(station.m_uid, now); return "obliterator_open"; }
        var view = ZNetScene.instance.FindInstance(station); var instance = view == null ? null : view.GetComponent<Incinerator>();
        return StationSupplyObliterator.LocallyBusy(instance) ? "obliterator_locally_busy" : null;
    }
    private static bool Room(ZDO station, SupplyIntake intake)
        => intake.Kind == SupplyKind.Fuel ? StationSupplyRules.FuelRoom(station.GetFloat(ZDOVars.s_fuel, intake.DefaultFuel), intake.Capacity) :
           intake.Kind == SupplyKind.Obliterator ? !Open(station) :
           intake.Kind == SupplyKind.Material ? StationSupplyRules.QueueRoom(station.GetInt(ZDOVars.s_queued), intake.Capacity) : station.GetInt(ZDOVars.s_content) == 0;
    private bool Drained(SupplyLease lease, float now, out string reason)
    {
        reason = "native_state_ready";
        if (!_authority.Holds(lease)) { reason = "lease_not_held"; return false; }
        if (!lease.Record.IsValid() || !ReferenceEquals(ZDOMan.instance.GetZDO(lease.Id), lease.Record)) { reason = "record_replaced_or_removed"; return false; }
        if (lease.Phase != 0) { if (lease.Record.GetOwner() == ZDOMan.GetSessionID()) return true; reason = "server_owner_not_observed"; return false; }
        if (lease.PreviousPeer == null || !_authority.CanSend(lease.PreviousPeer)) { reason = "previous_owner_transport_unavailable"; return false; }
        if (!_authority.Echoed(lease.Barrier)) { reason = "awaiting_owner_barrier"; return false; }
        if (!lease.SourceStateObserved)
        {
            if (lease.SourceEchoTime == 0) lease.SourceEchoTime = now;
            if (now - lease.SourceEchoTime >= 2) { _authority.ForgetBarrier(lease.Barrier); _counts.SourceDrains++; _authority.RequestSourceState(lease, now); }
            reason = "awaiting_source_state"; return false;
        }
        _authority.ForgetBarrier(lease.Barrier); lease.Phase = 1;
        return true;
    }
    private void Advance(SupplyTransaction t, float now)
    {
        if (t.Committed) { PublishComplete(t, now); return; }
        using (_diagnostics.Stage("world_discovery"))
            if (!_scope.RefreshAt(t.StationPosition, _settings.ScanRangeMeters.Value, includeRecords: false)) { End(t, now, _scope.UnavailableReason ?? "player_scope_unavailable"); return; }
        if (t.Station.PreviousPeer != null && !_authority.CanSend(t.Station.PreviousPeer) ||
            t.Chest.PreviousPeer != null && !_authority.CanSend(t.Chest.PreviousPeer))
        { End(t, now, "source_transport_unavailable"); return; }
        if (now - t.Started > 10) { End(t, now, "native_state_timeout"); return; }
        // Evaluate both drains; short-circuiting would starve the second source's retries.
        bool stationReady, chestReady;
        using (_diagnostics.Stage("ownership"))
        {
            stationReady = Drained(t.Station, now, out string stationWait); chestReady = Drained(t.Chest, now, out string chestWait);
            t.StationWait = stationWait; t.ChestWait = chestWait;
        }
        if (!stationReady || !chestReady) { Evidence(now, "draining_native_state", t.Station.Record, t.Intake, t.Chest.Record, t.Selection, t); return; }
        if (t.Station.ManualInteractionRequested || t.Chest.ManualInteractionRequested)
        { _counts.ManualYielded++; End(t, now, "manual_container_priority"); return; }
        using (_diagnostics.Stage("world_discovery"))
        {
            if (!_scope.RefreshAt(t.StationPosition, _settings.ScanRangeMeters.Value)) { End(t, now, _scope.UnavailableReason ?? "player_scope_unavailable"); return; }
            PrepareSelections();
        }
        var station = t.Station.Record; var chest = t.Chest.Record;
        string? changed = (station.GetPosition() - t.StationPosition).sqrMagnitude > .001f ? "station_moved" :
            (chest.GetPosition() - t.ChestPosition).sqrMagnitude > .001f ? "selected_chest_moved" :
            Quaternion.Angle(station.GetRotation(), t.StationRotation) > .1f ? "station_rotated" :
            station.GetLong(ZDOVars.s_creator) == 0 ? "station_creator_removed" : chest.GetLong(ZDOVars.s_creator) == 0 ? "chest_creator_removed" :
            !StationSupplyRules.NativeScale(station) ? "station_non_native_scale" : !StationSupplyRules.NativeScale(chest) ? "chest_non_native_scale" : null;
        if (changed != null) { End(t, now, changed); return; }
        changed = IntakeWait(station, t.Intake, now, out float remaining);
        if (changed != null) { End(t, now, changed, remaining: remaining); return; }
        if (Open(chest)) { _manual.Observe(chest.m_uid, now); End(t, now, "selected_chest_open"); return; }
        if (!_manual.Available(chest.m_uid, now, out remaining)) { End(t, now, "selected_chest_manual_grace", remaining: remaining); return; }
        var selected = Select(station, t.Intake, now, out var selection); t.Selection = selection;
        if (!ReferenceEquals(selected, chest)) { End(t, now, "selected_chest_changed"); return; }
        if (!Access(station, chest)) { End(t, now, "ward_access_denied"); return; }
        if (t.Intake.Kind == SupplyKind.Fermenter)
        {
            using var contextTiming = _diagnostics.Stage("native_context");
            t.Stage = "fermenter_context";
            if (_context == null || !_context.Prepare(_scope, station, now)) { Evidence(now, "fermenter_context_unavailable", station, t.Intake, chest, t.Selection, t, detail: _context?.WaitReason); return; }
            var view = ZNetScene.instance.FindInstance(station); var fermenter = view == null ? null : view.GetComponent<Fermenter>();
            if (fermenter == null) { Evidence(now, "fermenter_instance_unavailable", station, t.Intake, chest, t.Selection, t); return; }
            FermenterAccess.Cover(fermenter, 0, true);
            if (!FermenterAccess.Roof(fermenter)) { End(t, now, "fermenter_missing_roof"); return; }
            if (FermenterAccess.Exposed(fermenter)) { End(t, now, "fermenter_exposed"); return; }
        }
        var containerDefinition = _prefabs.Chests[chest.GetPrefab()];
        t.Stage = "source_inventory";
        var original = chest.GetByteArray(ZDOVars.s_items);
        if (original == null) { End(t, now, "source_inventory_unavailable"); return; }
        t.SourceInventory = original; t.SourceRevision = chest.DataRevision;
        int maximum = StationSupplyRules.VisitRoom(t.Intake.Kind, station.GetFloat(ZDOVars.s_fuel, t.Intake.DefaultFuel),
            station.GetInt(ZDOVars.s_queued), station.GetInt(ZDOVars.s_content), t.Intake.Capacity, _settings.MaxItemsPerVisit.Value);
        InventoryBatch? batch;
        using (_diagnostics.Stage("inventory_read"))
            batch = NativeInventory.WithdrawMany(original, containerDefinition.m_width, containerDefinition.m_height, t.Intake.Items, maximum, out t.Inventory, t.Intake.Kind == SupplyKind.Obliterator ? _disposalItem : null);
        if (batch == null) { End(t, now, t.Inventory.Status); return; }
        t.BatchSize = batch.Items.Count;
        var names = new string[batch.Items.Count]; var maximumStacks = new int[batch.Items.Count];
        for (int index = 0; index < batch.Items.Count; index++)
        {
            var resource = batch.Items[index]; string item = "";
            for (int i = 0; i < t.Intake.Items.Length; i++)
                if (t.Intake.Items[i] == resource.Prefab && resource.OriginalStack <= t.Intake.MaximumStacks[i]) { item = t.Intake.ItemNames[i]; maximumStacks[index] = t.Intake.MaximumStacks[i]; break; }
            if (t.Intake.Kind == SupplyKind.Obliterator)
            {
                var definition = HeimKit.Core.Prefabs.PrefabRegistry.Current.Prefab(resource.Prefab);
                var drop = HeimKit.Core.Prefabs.PrefabRegistry.Component<ItemDrop>(definition);
                if (definition != null && drop != null && resource.OriginalStack <= drop.m_itemData.m_shared.m_maxStackSize)
                { item = definition.name; maximumStacks[index] = drop.m_itemData.m_shared.m_maxStackSize; }
            }
            if (item.Length == 0) { End(t, now, "invalid_resource_stack", detail: "prefab=" + resource.Prefab + ",stack=" + resource.OriginalStack); return; }
            names[index] = item;
        }
        t.Item = names[0];
        var live = ZNetScene.instance.FindInstance(chest)?.GetComponent<Container>();
        bool transferred; string commitReason;
        if (t.Intake.Kind == SupplyKind.Obliterator)
        {
            t.Stage = "destination_inventory";
            var destinationOriginal = station.GetByteArray(ZDOVars.s_items);
            byte[]? destination; InventoryReadEvidence destinationEvidence; int inserted;
            using (_diagnostics.Stage("inventory_read"))
                destination = NativeInventory.InsertMany(destinationOriginal ?? NativeInventory.Empty(), t.Intake.InventoryWidth, t.Intake.InventoryHeight, batch.Items, maximumStacks, out inserted, out destinationEvidence);
            if (destination == null) { t.Inventory = destinationEvidence; End(t, now, destinationEvidence.Status, detail: "destination_inventory"); return; }
            batch.Limit(inserted); t.BatchSize = batch.Items.Count;
            var incinerator = ZNetScene.instance.FindInstance(station)?.GetComponent<Incinerator>();
            var targetContainer = incinerator == null ? null : incinerator.m_container;
            t.Stage = "commit";
            using (_diagnostics.Stage("commit"))
                transferred = StationSupplyContainerTransfer.TryCommit(t, original!, destinationOriginal, batch, destination, out commitReason,
                    live == null ? null : () => NativeInventory.Reload(live),
                    targetContainer == null ? null : () => NativeInventory.Reload(targetContainer));
        }
        else
        {
            t.Stage = "commit";
            using (_diagnostics.Stage("commit")) transferred = StationSupplyTransfer.TryCommit(t, original!, batch, names, ZNet.instance.GetTime().Ticks, out commitReason, live == null ? null : () => NativeInventory.Reload(live));
        }
        if (!transferred) { End(t, now, commitReason); return; }
        t.Committed = true; t.Station.Phase = 2; t.Chest.Phase = 2; t.PublishedAt = now;
        _counts.Transfers += t.BatchSize;
        t.Stage = "client_refresh";
        _diagnostics.Transferred(now, t.BatchSize);
        Evidence(now, "resource_transferred", station, t.Intake, chest, t.Selection, t);
        using (_diagnostics.Stage("client_refresh"))
        foreach (var peer in _players.Presence.Peers)
            if (peer.IsReady() && peer.m_uid != ZDOMan.GetSessionID())
            {
                // Fermenter.DelayedTap owns an unreplicated pending output on its
                // client instance. Keep that instance and publish only native fields.
                // One native destruction package, ordered snapshots and one echo
                // cover both records; all ready physical connections remain included.
                float echo = RefreshClient(t, peer, now);
                t.Echoes.Add((peer, echo));
            }
        PublishComplete(t, now);
    }
    private bool Connected(ZNetPeer peer)
    {
        var peers = _players.Presence.Peers;
        for (int i = 0; i < peers.Count; i++) if (ReferenceEquals(peers[i], peer)) return peers[i].IsReady();
        return false;
    }
    private void PublishComplete(SupplyTransaction t, float now)
    {
        using var measured = _diagnostics.Stage("client_refresh");
        bool complete = now - t.PublishedAt >= .5f;
        bool verbose = _diagnostics.VerboseEnabled;
        t.PendingRefresh = 0; t.PendingPeer = 0; t.PendingPeers.Clear();
        for (int i = 0; i < t.Echoes.Count; i++)
        {
            var echo = t.Echoes[i];
            if (!Connected(echo.Peer)) continue;
            // Reconnecting is still the same physical client/cache. Do not treat
            // it as disconnected or release before a fresh ordered refresh.
            if (echo.Nonce == 0 && _authority.CanSend(echo.Peer))
            {
                echo = (echo.Peer, RefreshClient(t, echo.Peer, now));
                t.Echoes[i] = echo;
            }
            if (!_authority.Echoed(echo.Nonce))
            { complete = false; t.PendingRefresh++; if (t.PendingPeer == 0) t.PendingPeer = echo.Peer.m_uid; if (verbose && !t.PendingPeers.Contains(echo.Peer.m_uid)) t.PendingPeers.Add(echo.Peer.m_uid); }
        }
        if (complete) End(t, now, "native_caches_refreshed");
        else if (now - t.PublishedAt > 10)
        {
            End(t, now, "client_refresh_timeout");
        }
        else Evidence(now, "client_refresh_wait", t.Station.Record, t.Intake, t.Chest.Record, t.Selection, t);
    }
    private float RefreshClient(SupplyTransaction transaction, ZNetPeer peer, float now)
    {
        if (!_authority.CanSend(peer)) return 0;
        if (transaction.Intake.Kind == SupplyKind.Fermenter) _counts.StateSnapshots++; else _counts.CacheResets++;
        _counts.CacheResets++;
        return _authority.RefreshClientPair(peer, transaction.Station, transaction.Chest, transaction.Intake.Kind == SupplyKind.Fermenter, now);
    }
    private void Observe(ZRpc rpc, ZPackage packet)
    {
        var peer = PlayerPeerLookup.Ready(_players.Presence.Peers, rpc);
        if (peer == null || !RoutedActivityReader.TryRead(packet, peer.m_uid, out var target, out int method)) return;
        if (_obliterator != null && StationSupplyObliterator.LeverMethod(method))
        { _obliterator.Observe(target, method, Time.unscaledTime); return; }
        if (ContainerRequests.IsRequest(method))
        {
            var record = ZDOMan.instance.GetZDO(target);
            if (record == null || !record.IsValid() ||
                (!_prefabs.Chests.ContainsKey(record.GetPrefab()) &&
                 !(_prefabs.Stations.TryGetValue(record.GetPrefab(), out var definition) && definition.Intakes[0].Kind == SupplyKind.Obliterator)) ||
                !ContainerRequests.Nearby(peer, record) ||
                !RoutedActivityReader.TryReadParameters(packet, peer.m_uid, out _, out _, out var parameters) || parameters == null || parameters.Size() != sizeof(long)) return;
            _manual.Observe(target, Time.unscaledTime); return;
        }
        if (method != Pong || !target.IsNone()) return;
        if (!RoutedActivityReader.TryReadParameters(packet, peer.m_uid, out _, out _, out var args) || args == null) return;
        float nonce = args.ReadSingle(); if (args.GetPos() == args.Size()) _authority.ObservePong(peer, nonce);
    }
    private void ManualRequest(ZDOID id, long peer, int method, string status, float now)
    {
        _manual.Observe(id, now);
        if (status == "queued") _counts.ManualQueued++;
        else if (status == "replayed") _counts.ManualReplayed++;
        else if (status != "already_open") { _counts.ManualUnavailable++; _counts.Observe("manual_request_" + status); }
    }
    private void Evidence(float now, string reason, ZDO? station = null, SupplyIntake? intake = null, ZDO? chest = null,
        SupplySelection selection = default, SupplyTransaction? transaction = null, string? detail = null, float remaining = 0, InventoryReadEvidence inventory = default, bool terminal = false)
    {
        _counts.Observe(reason);
        if (!_diagnostics.OptionalEnabled) return;
        using var measured = _diagnostics.Stage("diagnostics");
        // Typed identities avoid constructing strings for suppressed observations.
        bool draining = reason == "draining_native_state", placement = reason == "no_chest_at_intake" || reason == "selected_chest_changed";
        if (!_diagnostics.Begin(now, reason, station?.m_uid ?? default, intake?.Kind, chest?.m_uid ?? default,
            draining ? transaction?.StationWait : null, draining ? transaction?.ChestWait : null,
            placement ? selection.Rejection : null, placement ? selection.NearestRejected?.m_uid ?? default : default,
            placement ? selection.Selected?.m_uid ?? default : default, terminal)) return;
        string? name = station != null && _prefabs.Stations.TryGetValue(station.GetPrefab(), out var definition) ? definition.Name : null;
        _diagnostics.Evidence(now, reason, name, station, intake, chest, station?.GetRotation() ?? default, selection, transaction?.Inventory ?? inventory, transaction, detail, remaining, _scope, terminal);
    }
    internal LogFields FailureContext()
    {
        var t = _currentTransaction ?? (_active.Count == 0 ? null : _active[0]);
        if (t == null) return default;
        var identities = new System.Text.StringBuilder();
        foreach (var transaction in _active)
        {
            if (identities.Length != 0) identities.Append("; ");
            identities.Append(transaction.Station.Id).Append('/').Append(transaction.Intake.Kind).Append('/').Append(transaction.Chest.Id)
                .Append(" stage=").Append(transaction.Stage).Append(" committed=").Append(transaction.Committed);
        }
        return new[] { DiagnosticLog.Field("station", t.Station.Id), DiagnosticLog.Field("intake", t.Intake.Kind),
            DiagnosticLog.Field("chest", t.Chest.Id), DiagnosticLog.Field("stage", t.Stage), DiagnosticLog.FieldAlways("committed", t.Committed),
            DiagnosticLog.Field("active_transactions", identities.ToString()) };
    }
    private void EndAll(float now, string reason)
    {
        List<Exception>? errors = null;
        while (_active.Count != 0)
            try { End(_active[0], now, reason); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        _currentTransaction = null;
        if (errors != null) throw new AggregateException("StationSupply could not end all active transactions.", errors);
    }
    private void End(SupplyTransaction t, float now, string reason, string? detail = null, float remaining = 0)
    {
        if (!_active.Remove(t)) return;
        _currentTransaction = t;
        try
        {
            if (reason == "native_caches_refreshed") { _counts.RefreshCompleted++; _diagnostics.Recovered(t); }
            else if (!t.Committed) _counts.Cancelled++;
            if (StationSupplyDiagnostics.Actionable(reason)) _counts.Failures++;
            using (_diagnostics.Stage("diagnostics")) _diagnostics.Incident(now, t, reason);
            Evidence(now, reason, t.Station.Record, t.Intake, t.Chest.Record, t.Selection, t, detail, remaining, terminal: true);
        }
        finally
        {
            foreach (var echo in t.Echoes) _authority.ForgetBarrier(echo.Nonce);
            _authority.ForgetBarrier(t.Station.Barrier); _authority.ForgetBarrier(t.Chest.Barrier);
            bool released;
            using (_diagnostics.Stage("ownership"))
                released = ModuleCleanup.Run(_log, "StationSupply", () => _authority.Release(t.Chest.Id), () => _authority.Release(t.Station.Id));
            bool safe = released && SafeManualReplay(t, reason);
            bool requestsFinished = ModuleCleanup.Run(_log, "StationSupply",
                () => _authority.FinishContainerRequests(t.Chest, safe, now), () => _authority.FinishContainerRequests(t.Station, safe, now));
            if (!released || !requestsFinished) throw new InvalidOperationException("StationSupply could not release all transaction records or manual requests.");
            if (!t.Committed) _inputHistory.Remember(t.Chest.Record, t.Intake, t.SourceRevision, t.SourceInventory, t.Inventory);
            if (reason == "native_state_timeout" || reason == "station_ownership_unavailable" || reason == "chest_ownership_unavailable" || reason == "source_transport_unavailable")
            {
                bool stationReady = _authority.Preflight(t.Station.Record, out _), chestReady = _authority.Preflight(t.Chest.Record, out _);
                _retry.Failed(t.Station.Id, t.Intake, t.Chest.Id, RetryState(t.Station.Record, t.Chest.Record, stationReady, chestReady), now);
            }
        }
        if (ReferenceEquals(_currentTransaction, t)) _currentTransaction = null;
    }
    private StationSupplyRetry.State RetryState(ZDO station, ZDO chest, bool stationReady, bool chestReady) =>
        new(station, chest, _authority.OwnerConnection(station), _authority.OwnerConnection(chest), stationReady, chestReady);
    internal static bool SafeManualReplay(SupplyTransaction transaction, string reason) => transaction.Committed ?
        reason == "native_caches_refreshed" : transaction.Station.Phase >= 1 && transaction.Chest.Phase >= 1;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var messages = _messages; _messages = null;
        bool cleaned = ModuleCleanup.Run(_log, "StationSupply", () => messages?.Dispose(), () => EndAll(Time.unscaledTime, "processing_paused"),
            () => _authority.Dispose(), () => _scope.Dispose(), () => _context?.Dispose());
        _diagnostics.FlushTransfers(Time.unscaledTime, force: true);
        _stations.Clear(); _chestRecords.Clear(); _chests.Clear(); _wards.Clear(); _inputHistory.Clear(); _intakeOrder.Clear(); _manual.Clear(); _retry.Clear();
        if (!cleaned) throw new InvalidOperationException("StationSupply controller cleanup failed.");
    }
}
