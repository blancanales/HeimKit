using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.World;
using HeimKit.Configuration;
using HeimKit.Features.StationSupply;
using HeimKit.Features.StationSupply.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

internal static class StationSupplyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        Placement(); Inventory(); Transactions(); DisposalInventory(); DisposalTransactions(); Categories(); LeverQuiet(); Access(); Consumers(); Settings(); DiagnosticOutcomes(); Diagnostics(); Performance(); LoggingIsolation(); ManualAccess(); OutdoorPresence(); IntakeFairness(); BatchInventory(); BatchTransactions(); ActiveTransactions(); ScenePasses();
    }
    private sealed class ContainerPresence : IPlayerPresence
    {
        internal readonly List<ZNetPeer> Connected = new();
        internal int PeerReads;
        public bool? HasConnectedPlayers => Connected.Count != 0;
        public bool? HasJoinedPlayers => HasConnectedPlayers;
        public int? ConnectedCount => Connected.Count;
        public int? ReadyCount => ConnectedCount;
        public int? JoinedCount => ConnectedCount;
        public IReadOnlyList<ZNetPeer> Peers { get { PeerReads++; return Connected; } }
        public void Refresh() { }
        public void Clear() => Connected.Clear();
    }
    private static void OutdoorPresence()
    {
        using var world = new ReviewRegressionTests.World();
        var dungeon = world.Add(9950); var outside = world.Add(9951);
        var position = typeof(ZDO).GetField("m_position", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var owner = NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long));
        var interiorPoint = new Vector3(0, 5000, 0); var outdoorPoint = new Vector3(0, 30, 0); var station = new Vector3(4, 30, 0);
        position.SetValue(dungeon, interiorPoint); position.SetValue(outside, outdoorPoint); owner(dungeon, 81); owner(outside, 82);
        var presence = new ContainerPresence();
        var interiorPeer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); interiorPeer.m_uid = 81; interiorPeer.m_characterID = dungeon.m_uid;
        var outdoorPeer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); outdoorPeer.m_uid = 82; outdoorPeer.m_characterID = outside.m_uid;
        presence.Connected.Add(interiorPeer); presence.Connected.Add(outdoorPeer);
        int queries = 0;
        using var provider = new WorldObjectProvider(presence, nativeQuery: (_, _, _, _) => queries++, observe: false);
        using var scope = provider.CreateScope("StationSupply regression");
        try
        {
            provider.BeginCycle(true);
            Check(scope.RefreshAt(station, 64, includeRecords: false), "An outdoor actor could not establish anchored presence.");
            int peerReads = presence.PeerReads;
            for (int i = 0; i < 12; i++)
                Check(scope.RefreshAt(station, 64, includeRecords: false) && scope.Center == station && scope.Positions.Count == 2,
                    "An unrelated dungeon actor cancelled or recentered the station's drain.");
            Check(queries == 0 && peerReads > 0 && presence.PeerReads == peerReads && scope.PlayerNear(interiorPoint, 1),
                "Drain-only validation queried the world, repeated the census or lost dungeon clearance observations.");
            Check((int)typeof(PlayerWorldScope).GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scope)! == 0,
                "In-flight validation advanced the discovery cursor.");
            for (int i = 0; i < 6; i++) Check(scope.Refresh(64, outdoorOnly: true) && scope.Center == outdoorPoint,
                "Discovery wasted an outdoor visit on a dungeon actor.");
            Check(queries > 0 && presence.PeerReads == peerReads, "Discovery did not query or duplicated actor sampling.");
            Check(scope.RefreshAt(station, 64) && scope.Center == station, "Final placement revalidation lost its original station center.");
            int beforeWait = queries;
            Check(!scope.RefreshAt(interiorPoint, 64, includeRecords: false) && scope.UnavailableReason == "station_in_interior",
                "Separating sampling from selection enabled an interior station.");
            position.SetValue(outside, new Vector3(256, 30, 0)); provider.BeginCycle(true);
            Check(!scope.RefreshAt(station, 64, includeRecords: false) && scope.UnavailableReason == "no_player_near_station" && queries == beforeWait,
                "A departed outdoor actor or remote dungeon actor kept the furnace active.");
            position.SetValue(outside, interiorPoint); provider.BeginCycle(true);
            Check(!scope.Refresh(64, outdoorOnly: true) && scope.UnavailableReason == "no_outdoor_players" && queries == beforeWait && scope.Positions.Count == 2,
                "An all-dungeon roster triggered discovery or lost clearance observations.");
            position.SetValue(outside, outdoorPoint); ZDOExtraData.Set(outside.m_uid, ZDOVars.s_dead, 1); provider.BeginCycle(true);
            Check(!scope.RefreshAt(station, 64, includeRecords: false) && scope.UnavailableReason == "no_player_near_station",
                "A dead outdoor actor kept the furnace active.");
            ZDOExtraData.Set(outside.m_uid, ZDOVars.s_dead, 0); outdoorPeer.m_characterID = ZDOID.None; provider.BeginCycle(true);
            Check(!scope.RefreshAt(station, 64, includeRecords: false) && scope.UnavailableReason == "player_character_unavailable",
                "Unknown actor data was treated as reliable nearby presence.");
            outdoorPeer.m_characterID = outside.m_uid; position.SetValue(outside, new Vector3(float.NaN, 30, 0)); provider.BeginCycle(true);
            Check(!scope.RefreshAt(station, 64, includeRecords: false) && scope.UnavailableReason == "invalid_player_state",
                "Nonfinite player state passed anchored validation.");
            position.SetValue(outside, outdoorPoint); provider.BeginCycle(true);
            Check(scope.RefreshAt(station, 64, includeRecords: false), "Valid outdoor presence did not recover.");
            presence.Clear(); provider.BeginCycle(true);
            Check(!scope.RefreshAt(station, 64, includeRecords: false) && scope.UnavailableReason == "no_joined_players",
                "Empty presence did not stop anchored work.");
        }
        finally { ZDOExtraData.Release(dungeon, dungeon.m_uid); ZDOExtraData.Release(outside, outside.m_uid); }
        System.Console.WriteLine("PASS: dungeon occupants cannot cancel outdoor station drains; discovery skips interiors, retains all clearance positions, samples once and stops on absent/dead/unknown/nonfinite outdoor presence without drain queries.");
    }
    private static void IntakeFairness()
    {
        var order = new StationSupplyIntakeOrder();
        var records = new List<ZDO>();
        ZDO Station(uint id) { var record = Ward(id, Vector3.zero); records.Add(record); return record; }
        var smelter = Station(10000); var kilnA = Station(10001); var kilnB = Station(10002); var furnace = Station(10003); var secondFurnace = Station(10004);
        try
        {
            for (int visit = 0; visit < 16; visit++)
            {
                order.Start(smelter, 2); order.Start(kilnA, 1); order.Start(kilnB, 1);
                Check(order.Start(furnace, 2) == visit % 2 && order.Start(secondFurnace, 2) == visit % 2,
                    "The four-station layout or another furnace prevented independent material/fuel alternation.");
            }
            Check(order.Count == 3, "Single-intake kilns consumed ordering history.");
            order.Clear(); order.Start(furnace, 2);
            var replacement = Station(10003);
            Check(order.Start(replacement, 2) == 0, "A replacement record inherited the old furnace's intake order.");
            order.Start(replacement, 1);
            Check(order.Start(replacement, 2) == 0, "An intake-count change reused an incompatible cursor.");
            typeof(ZDO).GetField("m_prefab", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(replacement, 78);
            Check(order.Start(replacement, 2) == 0, "A changed native prefab reused its previous order.");
            order.Clear();
            for (uint i = 0; i < StationSupplyIntakeOrder.Capacity + 5; i++)
            {
                order.Start(Station(11000 + i), 2);
                Check(order.Start(furnace, 2) == i % 2, "Ordering pressure evicted an actively visited furnace.");
            }
            Check(order.Count == StationSupplyIntakeOrder.Capacity, "Intake history exceeded its bound.");
            for (int i = 0; i < 100; i++) order.Start(furnace, 2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) order.Start(furnace, 2);
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed per-station ordering allocated per visit.");
            order.Clear(); Check(order.Count == 0 && order.Start(furnace, 2) == 0, "Controller cleanup retained intake history.");
        }
        finally { foreach (var record in records) ZDOExtraData.Release(record, record.m_uid); }
        System.Console.WriteLine("PASS: independent station intake order alternates material/fuel in the four-station layout, resets on record/prefab/count changes, retains hot stations under bounded pressure and allocates nothing on warmed visits.");
    }
    private static void ManualAccess()
    {
        var quiet = new StationSupplyManualAccess(); var id = new ZDOID(765, 1); var other = new ZDOID(765, 2);
        Check(quiet.Available(id, 0, out _), "An untouched chest was unnecessarily paused.");
        quiet.Observe(id, 1);
        Check(!quiet.Available(id, 10, out float remaining) && remaining == 1 && quiet.Available(other, 10, out _), "Manual grace crossed chests or expired early.");
        quiet.Observe(id, 10);
        Check(!quiet.Available(id, 19, out _) && quiet.Available(id, 20, out _), "Continued manual use did not extend its grace.");
        quiet.Clear();
        for (uint i = 1; i <= 257; i++) quiet.Observe(new ZDOID(766, i), 30);
        Check(!quiet.Available(other, 39, out _) && quiet.Available(other, 40, out _), "Signal overflow removed recent protection or paused forever.");
        Check(!quiet.Available(other, 5, out remaining) && remaining == 10 && quiet.Available(other, 15, out _) &&
            !quiet.Available(other, float.NaN, out _), "Clock rollback/invalid time bypassed manual protection.");

        using var world = new ReviewRegressionTests.World();
        var actor = world.Add(9930); var chest = world.Add(9931);
        var owner = NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long));
        owner(actor, 81);
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 81; peer.m_characterID = actor.m_uid;
        var presence = new ContainerPresence(); presence.Connected.Add(peer);
        var lease = new SupplyLease(chest);
        var parameters = new ZPackage(); parameters.Write(123456L);
        var packet = new ZRoutedRpc.RoutedRPCData { m_msgID = 876, m_senderPeerID = 81, m_targetPeerID = 42,
            m_targetZDO = chest.m_uid, m_methodHash = "RPC_RequestOpen".GetStableHashCode(), m_parameters = parameters };
        Check(!ContainerRequests.Shape(packet, peer, lease), "A non-opted-in lease deferred an interaction.");
        lease.ManualContainerAccess = true;
        Check(ContainerRequests.Shape(packet, peer, lease) && ContainerRequests.Nearby(peer, chest), "A valid nearby native request was rejected.");
        foreach (string method in new[] { "RPC_RequestStack", "RPC_RequestTakeAll" })
        { packet.m_methodHash = method.GetStableHashCode(); Check(ContainerRequests.Shape(packet, peer, lease), "A supported container operation was rejected."); }
        packet.m_methodHash = "RPC_RequestOpen".GetStableHashCode();
        packet.m_senderPeerID = 82; Check(!ContainerRequests.Shape(packet, peer, lease), "A spoofed sender was accepted."); packet.m_senderPeerID = 81;
        packet.m_targetZDO = actor.m_uid; Check(!ContainerRequests.Shape(packet, peer, lease), "A different target was accepted."); packet.m_targetZDO = chest.m_uid;
        packet.m_methodHash = "RPC_RequestIncinerate".GetStableHashCode(); Check(!ContainerRequests.Shape(packet, peer, lease), "A destructive request became a deferred container interaction.");
        packet.m_methodHash = "RPC_RequestOpen".GetStableHashCode();
        packet.m_parameters = new ZPackage(); packet.m_parameters.Write(1);
        Check(!ContainerRequests.Shape(packet, peer, lease), "Truncated native arguments were accepted.");
        packet.m_parameters.Write(2); packet.m_parameters.Write(3);
        Check(!ContainerRequests.Shape(packet, peer, lease), "Trailing native arguments were accepted."); packet.m_parameters = parameters;
        lease.StaticControl = false; Check(!ContainerRequests.Shape(packet, peer, lease), "Animal leases opted into container replay."); lease.StaticControl = true;
        owner(actor, 82); Check(!ContainerRequests.Nearby(peer, chest), "An unowned character could reserve a chest."); owner(actor, 81);
        ZDOExtraData.Set(actor.m_uid, ZDOVars.s_dead, 1);
        Check(!ContainerRequests.Nearby(peer, chest), "A dead actor could reserve a chest."); ZDOExtraData.Set(actor.m_uid, ZDOVars.s_dead, 0);
        world.Move(actor, 1, 0); Check(!ContainerRequests.Nearby(peer, chest), "A remote actor could reserve a chest."); world.Move(actor, 0, 0);

        var authority = (ObjectTransferAuthority)RuntimeHelpers.GetUninitializedObject(typeof(ObjectTransferAuthority));
        var reports = new List<string>(); bool retained = true;
        using (var requests = new ContainerRequests(authority, presence, _ => retained, (_, _, _, status, _) => reports.Add(status)))
        {
            Check(requests.Capture(peer, lease, packet, 0) && lease.ManualInteractionRequested && requests.Contains(chest.m_uid), "A fenced open was lost instead of signalling manual priority.");
            Check(requests.Capture(peer, lease, packet, 1) && reports.SequenceEqual(new[] { "queued" }), "Repeated clicks queued multiple replays.");
            requests.Finish(new SupplyLease(chest), false, 1);
            Check(requests.Contains(chest.m_uid), "A different lease completed the original request.");
            requests.Finish(lease, true, 1);
            requests.Tick(2); requests.Tick(3);
            Check(requests.Contains(chest.m_uid) && reports.Count == 1, "An interaction bypassed a remaining ownership fence.");
            retained = false; requests.Tick(4);
            Check(requests.Contains(chest.m_uid) && reports.Count == 1, "Replay invented an owner for an unassigned chest.");
            var replacementPeer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); replacementPeer.m_uid = peer.m_uid; replacementPeer.m_characterID = actor.m_uid;
            presence.Connected.Clear(); presence.Connected.Add(replacementPeer); requests.Tick(5);
            Check(!requests.Contains(chest.m_uid) && reports[^1] == "unavailable" && packet.m_msgID == 876 && packet.m_senderPeerID == 81 && packet.m_parameters.GetPos() == 8,
                "Reconnect reused an old request, or queued evidence changed its original packet.");
            presence.Connected.Clear(); presence.Connected.Add(peer);
            Check(requests.Capture(peer, lease, packet, 6), "A fresh request after reconnect could not be queued.");
            presence.Connected.Clear(); requests.Finish(lease, false, 7);
            Check(!requests.Contains(chest.m_uid) && reports[^1] == "unsafe", "An unsafe cancellation retained a replay.");
            presence.Connected.Add(peer); requests.Capture(peer, lease, packet, 8); presence.Connected.Clear();
        }
        var transaction = new SupplyTransaction { Station = new(chest), Chest = lease };
        Check(!StationSupplyController.SafeManualReplay(transaction, "manual_container_priority"), "An undrained transaction allowed replay.");
        transaction.Station.Phase = 1;
        Check(!StationSupplyController.SafeManualReplay(transaction, "manual_container_priority"), "One completed drain allowed replay.");
        transaction.Chest.Phase = 1;
        Check(StationSupplyController.SafeManualReplay(transaction, "manual_container_priority"), "Both confirmed drains could not yield safely.");
        transaction.Committed = true;
        Check(!StationSupplyController.SafeManualReplay(transaction, "client_refresh_timeout") && StationSupplyController.SafeManualReplay(transaction, "native_caches_refreshed"), "A committed transfer skipped its client refresh fence.");
        var counts = new SupplyCounts { ManualQueued = 1, ManualYielded = 1, ManualReplayed = 1, ManualUnavailable = 1 };
        Check(counts.HasActivity, "Manual outcomes disappeared from operational counts."); counts.Reset(); Check(!counts.HasActivity, "Manual counts survived a reported interval.");
        ZDOExtraData.Release(actor, actor.m_uid); ZDOExtraData.Release(chest, chest.m_uid);
        System.Console.WriteLine("PASS: manual container access has bounded grace, authenticated nearby intent, single deferred requests, fence/owner/reconnect safety and confirmed-drain/refresh gating.");
    }
    private static void LoggingIsolation()
    {
        DiagnosticLog.StartSession(); LogCapture.ResetVerbosity();
        using var log = new ManualLogSource("Supply incident checks");
        var records = new List<(LogLevel Level, string Text)>(); log.LogEvent += (_, e) => records.Add((e.Level, LogCapture.Text(e.Data)));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var settings = new StationSupplySettings(config, log);
        var diagnostics = new StationSupplyDiagnostics(log, settings);
        var transaction = new SupplyTransaction { Station = new((ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO))) { Id = new ZDOID(321, 1) },
            Chest = new((ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO))) { Id = new ZDOID(321, 2) },
            Definition = new SupplyStation { Name = "smelter" }, Intake = new SupplyIntake { Kind = SupplyKind.Material },
            Stage = "source_state_drain", StationWait = "native_state_ready", ChestWait = "awaiting_source_state" };
        try
        {
            // Actionable incidents remain available with debugging disabled and after optional exhaustion.
            diagnostics.Incident(0, transaction, "native_state_timeout"); diagnostics.Incident(1, transaction, "native_state_timeout");
            Check(records.Count == 1 && records[0].Level == LogLevel.Warning && records[0].Text.Contains("321:1") && records[0].Text.Contains("awaiting_source_state") && records[0].Text.Contains("committed: false"), "A timeout was hidden with debug off or lost object/stage identity.");
            diagnostics.Incident(301, transaction, "native_state_timeout");
            Check(records.Count == 2 && records[1].Text.Contains("Suppressed repeats: 1"), "Repeated incident reminders lost their cadence/count.");
            transaction.ChestWait = "previous_owner_disconnected"; diagnostics.Incident(302, transaction, "native_state_timeout");
            Check(records.Count == 3, "A changed native failure was suppressed.");
            diagnostics.Recovered(transaction); diagnostics.Recovered(transaction);
            Check(records.Count == 4 && records[3].Level == LogLevel.Info && records[3].Text.Contains("recovered"), "Recovery was missing or repeated.");
            diagnostics.Incident(303, transaction, "native_state_timeout"); Check(records.Count == 5, "Recurrence after recovery was suppressed.");
            settings.DebugMode.Value = true;
            for (uint i = 0; i < 6; i++) diagnostics.Begin(304, "selected_chest_open", new ZDOID(322, i));
            for (uint i = 0; i < 4; i++) diagnostics.Begin(304, "native_state_timeout", new ZDOID(323, i), terminal: true);
            transaction.Committed = true; transaction.Stage = "client_refresh"; transaction.PendingRefresh = 2; transaction.PendingPeer = 42;
            diagnostics.Incident(304, transaction, "client_refresh_timeout");
            Check(records.Count == 6 && records[5].Level == LogLevel.Warning && records[5].Text.Contains("committed: true") && records[5].Text.Contains("first peer: 42"), "Exhausted optional budgets hid a committed refresh timeout.");
            for (uint i = 0; i < 300; i++) { transaction.Station.Id = new ZDOID(324, i + 1); diagnostics.Incident(305, transaction, "client_refresh_timeout"); }
            var incidents = (System.Collections.IDictionary)typeof(StationSupplyDiagnostics).GetField("_incidents", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(diagnostics)!;
            Check(incidents.Count == 256, "Incident retention exceeded its identity bound.");
            records.Clear(); settings.DebugMode.Value = false;
            diagnostics.PreflightIncident(306, "smelter", new ZDOID(325, 1), SupplyKind.Material, new ZDOID(325, 2), "invalid_source_inventory");
            diagnostics.PreflightIncident(307, "smelter", new ZDOID(325, 1), SupplyKind.Material, new ZDOID(325, 2), "invalid_source_inventory");
            Check(records.Count == 1 && records[0].Level == LogLevel.Warning && records[0].Text.Contains("source_inventory") && records[0].Text.Contains("committed: false"), "Malformed preflight inventory was silent or bypassed incident deduplication.");
            DiagnosticLog.StartSession(); records.Clear(); diagnostics = new(log, settings);
            var counts = new SupplyCounts { Transactions = 20, Transfers = 18, Cancelled = 2, Failures = 1, SourceDrains = 40, CacheResets = 36, RefreshCompleted = 17 };
            for (int i = 0; i < 100; i++) { counts.Observe("no_chest_at_intake"); counts.Observe("resource_transferred"); }
            settings.DebugMode.Value = false; diagnostics.Scope(0, 3, 76, counts);
            Check(records.Count == 0 && counts.HasActivity, "Suppression lost operational counts or emitted optional output.");
            settings.DebugMode.Value = true; diagnostics.Scope(1, 3, 76, counts);
            Check(records.Count == 2 && records[1].Text.Contains("transfers=18") && records[1].Text.Contains("failures=1") && records[1].Text.Contains("cache_resets=36") && records[1].Text.Contains("blocked_checks=no_chest_at_intake:100") && !records[1].Text.Contains("resource_transferred") && !records[1].Text.Contains("omitted_fields=") && !counts.HasActivity, "Activity summaries lost outcomes/blocked counts or included routine stage chatter.");
            counts.Transfers = 5;
            for (int i = 0; i < 60; i++) DiagnosticLog.Write(log, "StationSupply", CoreLogTopics.Scope, "Budget probe.", level: LogLevel.Debug);
            diagnostics.Scope(65, 3, 76, counts); Check(counts.HasActivity, "Global optional exhaustion discarded pending activity counts.");
            DiagnosticLog.StartSession(); diagnostics.Scope(65, 3, 76, counts); Check(!counts.HasActivity && records[^1].Text.Contains("transfers=5"), "Deferred activity counts were not reported after capacity returned.");
            records.Clear(); DiagnosticLog.StartSession();
            var timing = new ProcessingTiming(log, "StationSupply", settings.DebugLogging); timing.RegisterStages("inventory_read", "client_refresh");
            Func<int> scope = static () => 79;
            using (timing.MeasureValue(0, scope)) { using (timing.MeasureStage("inventory_read")) { } using (timing.MeasureStage("client_refresh")) { } }
            Check(records.Count == 2 && records[1].Text.Contains("inventory_read calls=1 avg/max_ms=") && records[1].Text.Contains("client_refresh calls=1 avg/max_ms="), "The combined stage record lost timing data from the pass reporting window.");
            for (int i = 0; i < 100; i++) { using var stage = timing.MeasureStage("inventory_read"); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { using var stage = timing.MeasureStage("inventory_read"); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before && records.Count == 2, "Warmed stage profiling allocates or emits per-call records.");
            settings.DebugMode.Value = false; using (timing.MeasureStage("client_refresh")) { } using (timing.MeasureValue(65, scope)) { }
            Check(records.Count == 2, "Stage timing ignored debug suppression.");
        }
        finally { LogCapture.ResetVerbosity(); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: incident warnings survive disabled/exhausted debugging with identity, reminders and recovery; bounded activity counters survive suppression, and allocation-free stage timings aggregate within the pass window.");
    }
    private static void Performance()
    {
        var history = new StationSupplyInputHistory(); var port = new SupplyIntake { Kind = SupplyKind.Material };
        var otherPort = new SupplyIntake { Kind = SupplyKind.Fuel };
        var chest = Ward(9000, Vector3.zero); chest.DataRevision = 7;
        var inventory = Contents(Item(11, 1, 0)); ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, inventory);
        var rejected = new InventoryReadEvidence { Status = "no_compatible_resource", Stacks = 1, Rejected = 1, FirstRejected = 11 };
        var retained = new List<ZDO>();
        try
        {
            history.Remember(chest, port, chest.DataRevision, inventory, rejected);
            Check(history.TryGet(chest, port, out var evidence) && evidence.Status == rejected.Status && evidence.FirstRejected == 11 && !history.TryGet(chest, otherPort, out _), "A confirmed input miss was lost or crossed intake definitions.");
            chest.OwnerRevision++;
            Check(history.TryGet(chest, port, out _), "Owner assignment alone triggered another inventory probe despite unchanged native data.");
            var prepared = StationSupplyRules.Placement(Vector3.zero, new Quaternion(0, 0, 0, 1), new Vector3(1, 4, 0), 2, 1.5f);
            for (int i = 0; i < 100; i++) { history.TryGet(chest, port, out _); StationSupplyRules.Distance(prepared, new Vector3(2, 0, 0), out _); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                if (!history.TryGet(chest, port, out _) || !StationSupplyRules.Distance(prepared, new Vector3(2, 0, 0), out _).HasValue) throw new Exception("A warmed eligibility check changed.");
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed cached-input and prepared-intake checks allocate per pass.");
            chest.DataRevision++;
            Check(!history.TryGet(chest, port, out _) && history.Count == 0, "Changed native data retained a cached rejection.");
            history.Remember(chest, port, chest.DataRevision - 1, inventory, rejected);
            Check(history.Count == 0, "A revision changed during release was cached.");
            history.Remember(chest, port, chest.DataRevision, (byte[])inventory.Clone(), rejected);
            Check(history.Count == 0, "An inventory changed during release was cached.");
            history.Remember(chest, port, chest.DataRevision, inventory, new InventoryReadEvidence { Status = "resource_selected" });
            Check(history.Count == 0, "A successful withdrawal was cached.");
            history.Remember(chest, port, chest.DataRevision, inventory, rejected);
            var replacement = Ward(9000, Vector3.zero); replacement.DataRevision = chest.DataRevision;
            Check(!history.TryGet(replacement, port, out _), "A replacement object inherited an earlier chest's negative decision.");
            history.Remember(chest, port, chest.DataRevision, inventory, rejected);
            typeof(ZDO).GetField("m_prefab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chest, -1);
            Check(!history.TryGet(chest, port, out _), "A destroyed chest retained a negative decision.");
            for (uint i = 0; i < 300; i++)
            {
                var record = Ward(9100 + i, Vector3.zero); record.DataRevision = 1; retained.Add(record);
                ZDOExtraData.Set(record.m_uid, ZDOVars.s_items, inventory);
                history.Remember(record, port, 1, inventory, rejected);
            }
            Check(history.Count == StationSupplyInputHistory.Capacity && !history.TryGet(retained[0], port, out _) && history.TryGet(retained[^1], port, out _), "Input history exceeded its cap or evicted the newest decision.");
            history.Clear(); Check(history.Count == 0, "Controller cleanup retained input decisions.");
        }
        finally { ZDOExtraData.Release(chest, chest.m_uid); foreach (var record in retained) ZDOExtraData.Release(record, record.m_uid); }
        // Compare prepared geometry with the original rule, independently of its new wrapper.
        float? Original(Vector3 station, Quaternion rotation, Vector3 input, Vector3 point)
        {
            if (Math.Abs(point.y - station.y) > 1.5f) return null;
            var anchor = rotation * input; anchor.y = 0;
            var offset = point - station - anchor; offset.y = 0;
            if (anchor.sqrMagnitude > .0625f && Vector3.Dot(offset, anchor.normalized) < -.25f) return null;
            return offset.sqrMagnitude <= 4 ? offset.sqrMagnitude : null;
        }
        var origin = new Vector3(100, 20, 200); var input = new Vector3(-1.6f, 4.3f, 0);
        foreach (var rotation in new[] { new Quaternion(0, 0, 0, 1), new Quaternion(0, .70710677f, 0, .70710677f), new Quaternion(0, 1, 0, 0) })
        {
            var placement = StationSupplyRules.Placement(origin, rotation, input, 2, 1.5f);
            for (int x = -16; x <= 16; x++) for (int z = -16; z <= 16; z++) for (int y = -2; y <= 2; y++)
            {
                var point = origin + new Vector3(x * .25f, y, z * .25f);
                Check(StationSupplyRules.Distance(placement, point, out _) == Original(origin, rotation, input, point), "Prepared geometry changed a rotated/height/side/range boundary.");
            }
        }
        var canonical = NativeAccess.Method<Func<byte[], int, int, byte[]>>(typeof(NativeInventory), "CanonicalItem", typeof(byte[]), typeof(int), typeof(int));
        var same = NativeAccess.Method<Func<byte[], int, int, byte[], bool>>(typeof(NativeInventory), "SameItem", typeof(byte[]), typeof(int), typeof(int), typeof(byte[]));
        var random = new System.Random(7021);
        for (int flags = 0; flags < 256; flags++)
        {
            var bytes = new byte[48]; random.NextBytes(bytes); bytes[10] = (byte)flags;
            var expected = canonical(bytes, 3, 43);
            Check(same(bytes, 3, 43, expected), "An identical normalized item stopped matching.");
            for (int index = 3; index < 43; index++)
            {
                bytes[index] ^= 0x41;
                Check(same(bytes, 3, 43, expected) == canonical(bytes, 3, 43).SequenceEqual(expected), "Allocation-free metadata comparison changed normalization at flag " + flags + ", byte " + index);
                bytes[index] ^= 0x41;
            }
        }
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Supply performance checks");
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var settings = new StationSupplySettings(config, log); settings.DebugMode.Value = true;
        var diagnostics = new StationSupplyDiagnostics(log, settings); var id = new ZDOID(99, 1);
        try
        {
            Check(diagnostics.Begin(0, "draining_native_state", id, SupplyKind.Fuel, chestWait: "awaiting_owner_barrier"), "Initial typed wait was denied.");
            for (int i = 0; i < 100; i++) diagnostics.Begin(1, "draining_native_state", id, SupplyKind.Fuel, chestWait: "awaiting_owner_barrier");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) diagnostics.Begin(1, "draining_native_state", id, SupplyKind.Fuel, chestWait: "awaiting_owner_barrier");
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Suppressed native wait diagnostics allocate semantic identity strings.");
            Check(diagnostics.Begin(2, "draining_native_state", id, SupplyKind.Fuel, chestWait: "awaiting_source_state"), "A typed ownership stage change was suppressed.");
            Check(diagnostics.Begin(3, "no_chest_at_intake", id, SupplyKind.Fuel, candidate: new ZDOID(99, 2), rejection: "chest_height_exceeded") &&
                diagnostics.Begin(4, "no_chest_at_intake", id, SupplyKind.Fuel, candidate: new ZDOID(99, 3), rejection: "chest_height_exceeded"), "Distinct typed rejected candidates were suppressed.");
            var timing = new ProcessingTiming(log, "StationSupply", settings.DebugLogging);
            Func<int> scope = static () => 1;
            using (timing.MeasureValue(0, scope)) { }
            for (int i = 0; i < 100; i++) { using var sample = timing.MeasureValue(1, scope); }
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { using var sample = timing.MeasureValue(1, scope); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed value timing scopes allocate measurement objects or closures.");
            default(ProcessingTiming.TimedScope).Dispose();
        }
        finally { LogCapture.ResetVerbosity(); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: prepared intake geometry preserves original boundaries; unchanged input misses avoid reprobes without allocations, invalidate on native revision/replacement/destruction, stay bounded, and typed diagnostic deduplication allocates no strings.");
    }
    private static void DiagnosticOutcomes()
    {
        var origin = Vector3.zero; var rotation = new Quaternion(0, 0, 0, 1); var intake = new Vector3(1, 4, 0);
        Check(StationSupplyRules.Distance(origin, rotation, intake, new Vector3(2, 2, 0), 2, 1.5f, out var reason) == null && reason == "chest_height_exceeded", "Height rejection lost its reason.");
        Check(StationSupplyRules.Distance(origin, rotation, intake, new Vector3(-1, 0, 0), 3, 1.5f, out reason) == null && reason == "chest_on_inward_side", "Wrong-side rejection lost its reason.");
        Check(StationSupplyRules.Distance(origin, rotation, intake, new Vector3(4, 0, 0), 2, 1.5f, out reason) == null && reason == "chest_range_exceeded", "Range rejection lost its reason.");
        var original = Contents(Item(11, 2, 0), Item(22, 1, 1));
        Check(NativeInventory.WithdrawOne(original, 5, 2, new[] { 999 }, out var evidence) == null && evidence.Status == "no_compatible_resource" && evidence.Stacks == 2 && evidence.Rejected == 2 && evidence.FirstRejected == 11, "Incompatible resources are not distinguishable from invalid inventories.");
        Check(NativeInventory.WithdrawOne(original, 5, 2, Array.Empty<int>(), out evidence, _ => false) == null && evidence.Status == "no_allowed_item" && evidence.Rejected == 2, "Category exclusion lost its own outcome.");
        Check(NativeInventory.WithdrawOne(Contents(), 5, 2, new[] { 11 }, out evidence) == null && evidence.Status == "empty_inventory", "Empty storage is not distinguishable from wrong resources.");
        var unsupported = Contents(); unsupported[0] = 1;
        Check(NativeInventory.WithdrawOne(unsupported, 5, 2, new[] { 11 }, out evidence) == null && evidence.Status == "unsupported_inventory_format", "Unsupported formats lost their own outcome.");
        Check(NativeInventory.WithdrawOne(original.Take(original.Length - 1).ToArray(), 5, 2, new[] { 11 }, out evidence) == null && evidence.Status == "invalid_inventory", "Malformed data looks like an ordinary filter miss.");
        var withdrawal = NativeInventory.WithdrawOne(original, 5, 2, new[] { 11 }, out evidence)!;
        Check(evidence.Status == "resource_selected" && evidence.Stacks == 2 && evidence.Rejected == 1 && original.SequenceEqual(Contents(Item(11, 2, 0), Item(22, 1, 1))), "Diagnostic parsing changed source bytes or rejected-stack counts.");
        Check(NativeInventory.InsertOne(Contents(Item(22, 1, 0)), 1, 1, withdrawal, 10, out evidence) == null && evidence.Status == "inventory_full" && evidence.Stacks == 1, "A full destination looks corrupt.");
        Check(NativeInventory.InsertOne(new byte[] { 1 }, 1, 1, withdrawal, 10, out evidence) == null && evidence.Status == "invalid_inventory", "Malformed destinations look full.");
        var gate = new StationSupplyObliterator(0, 10); var id = new ZDOID(49, 800);
        Check(!gate.Quiet(id, 5, out reason, out float remaining) && reason == "obliterator_startup_quiet" && remaining == 5, "Startup quiet diagnostics lost their reason or remaining time.");
        gate.Observe(id, "RPC_RequestIncinerate".GetStableHashCode(), 12);
        Check(!gate.Quiet(id, 15, out reason, out remaining) && reason == "obliterator_lever_quiet" && remaining == 7, "Lever quiet diagnostics lost their reason or remaining time.");
        var authority = (ObjectTransferAuthority)RuntimeHelpers.GetUninitializedObject(typeof(ObjectTransferAuthority));
        typeof(ObjectTransferAuthority).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(authority, true);
        Check(!authority.Acquire(new SupplyLease((ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO))), 0, out reason) && reason == "authority_disposed", "Ownership failure lost its precise reason.");
        System.Console.WriteLine("PASS: StationSupply diagnostics distinguish placement, empty/incompatible/category-filtered/corrupt inventory, full destinations, ownership failures and native lever quiet waits without consuming source bytes.");
    }
    private static void Diagnostics()
    {
        DiagnosticLog.StartSession(); LogCapture.ResetVerbosity();
        using var log = new ManualLogSource("StationSupply diagnostic checks");
        var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-supply-debug-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var settings = new StationSupplySettings(config, log); settings.DebugMode.Value = true;
        var diagnostics = new StationSupplyDiagnostics(log, settings);
        var a = new ZDOID(91, 1); var b = new ZDOID(91, 2); var chest = new ZDOID(91, 3);
        Check(diagnostics.Begin(0, "no_compatible_resource", a, SupplyKind.Material, chest) &&
            !diagnostics.Begin(1, "no_compatible_resource", a, SupplyKind.Material, chest) &&
            diagnostics.Begin(1, "no_compatible_resource", b, SupplyKind.Material, chest) &&
            diagnostics.Begin(1, "no_compatible_resource", a, SupplyKind.Fuel, chest), "One station/intake suppressed another or repeated output was not suppressed.");
        Check(diagnostics.Begin(2, "selected_chest_open", a, SupplyKind.Material, chest) &&
            diagnostics.Begin(3, "no_compatible_resource", a, SupplyKind.Material, chest), "A changed/recovered condition was permanently suppressed.");
        Check(diagnostics.Begin(4, "wait", new ZDOID(92, 1)), "Blocked allowance ended early.");
        Check(!diagnostics.Begin(5, "not_yet_logged", new ZDOID(93, 1)), "Blocked samples exceeded six records.");
        for (uint i = 0; i < 2; i++) Check(diagnostics.Begin(5, "draining_native_state", new ZDOID(95, i + 1)), "Progress allowance ended early.");
        Check(!diagnostics.Begin(5, "client_refresh_wait", new ZDOID(95, 3)), "Progress samples exceeded two records.");
        for (uint i = 0; i < 4; i++) Check(diagnostics.Begin(5, "native_state_timeout", new ZDOID(96, i + 1), terminal: true), "Routine samples consumed the reserved outcome allowance.");
        Check(!diagnostics.Begin(5, "native_state_timeout", new ZDOID(96, 5), terminal: true) && !diagnostics.SelectionReady(5), "Supply evidence exceeded twelve records per minute or continued collecting optional selection detail.");
        diagnostics.Scope(5, 1, 1);
        Check(lines.Count == 1 && lines[0].Contains("omitted=blocked:1,outcomes:1,progress:1"), "The scope summary hides budget omissions.");
        Check(diagnostics.Begin(65, "not_yet_logged", new ZDOID(93, 1)), "A budget-denied event was remembered as already logged.");
        settings.DebugMode.Value = false;
        Check(!diagnostics.Begin(65, "disabled", a), "Feature debug suppression was bypassed.");
        settings.DebugMode.Value = true;
        DiagnosticLog.StartSession(); lines.Clear(); diagnostics = new(log, settings);
        var station = Ward(810, new Vector3(100, 20, 200)); var source = Ward(811, new Vector3(103, 22, 200));
        var port = new SupplyIntake { Kind = SupplyKind.Fuel, LocalPoint = new Vector3(1, 4, 0), Capacity = 20 };
        var selection = new SupplySelection { Checked = true, CountsMeasured = true, NearestRejected = source, Rejection = "chest_height_exceeded", WrongHeight = 1 };
        try
        {
            Check(diagnostics.Begin(0, "no_chest_at_intake", station.m_uid, port.Kind), "Placement record was denied.");
            diagnostics.Evidence(0, "no_chest_at_intake", "smelter", station, port, null, new Quaternion(0, 0, 0, 1), selection, default, null, null, 0);
            Check(lines.Count == 1 && lines[0].Contains("intake=Fuel") && lines[0].Contains("rejected_distance_m=2.0") && lines[0].Contains("rejected_height_m=2.0") && lines[0].Contains("rejected_reason=chest_height_exceeded") && !lines[0].Contains("omitted_fields="), "Normal placement evidence lost measured values or exceeded eight fields.");
            config.Bind("Diagnostics", "DebugVerbosity", DebugVerbosity.Normal).Value = DebugVerbosity.Verbose;
            Check(diagnostics.Begin(1, "station_has_no_room", station.m_uid, port.Kind), "Verbose capacity record was denied.");
            diagnostics.Evidence(1, "station_has_no_room", "smelter", station, port, null, new Quaternion(0, 0, 0, 1), default, default, null, null, 0);
            Check(lines.Count == 2 && lines[1].Contains("fuel=0") && lines[1].Contains("capacity=20") && lines[1].Contains("station_position=100.0,20.0,200.0") && lines[1].Contains("range_m=2.0") && !lines[1].Contains("omitted_fields="), "Verbose context or meaningful zero values disappeared.");
            Check(lines[1].Contains("selection_checked=false") && !lines[1].Contains("eligible_chests=") && !lines[1].Contains("wrong_side_chests="), "Unmeasured chest selection was reported as zero eligible chests.");
            var transaction = new SupplyTransaction { Station = new(station) { PreviousOwner = 701 }, Chest = new(source) { PreviousOwner = 702 }, Intake = port,
                Stage = "source_state_drain", StationWait = "native_state_ready", ChestWait = "awaiting_source_state" };
            Check(diagnostics.Begin(2, "draining_native_state", station.m_uid, port.Kind, source.m_uid), "Owner wait record was denied.");
            diagnostics.Evidence(2, "draining_native_state", "smelter", station, port, source, new Quaternion(0, 0, 0, 1), default, default, transaction, null, 0);
            Check(lines.Count == 3 && lines[2].Contains("chest_wait=awaiting_source_state") && lines[2].Contains("station_previous_owner=701") && lines[2].Contains("chest_previous_owner=702") && lines[2].Contains("committed=false"), "Ownership wait evidence lost the blocked object or native state.");
            transaction.Committed = true; transaction.Stage = "client_refresh"; transaction.PublishedAt = 2;
            transaction.BatchSize = 7; transaction.PendingRefresh = 2; transaction.PendingPeer = 710; transaction.PendingPeers.AddRange(new long[] { 710, 711 });
            Check(diagnostics.Begin(3, "client_refresh_timeout", station.m_uid, port.Kind, source.m_uid, terminal: true), "Refresh timeout record was denied.");
            diagnostics.Evidence(3, "client_refresh_timeout", "smelter", station, port, source, new Quaternion(0, 0, 0, 1), default, default, transaction, null, 0, terminal: true);
            Check(lines.Count == 4 && lines[3].Contains("pending_refresh=2") && lines[3].Contains("pending_peers=710,711") && lines[3].Contains("committed=true") && lines[3].Contains("batch_size=7"), "Refresh evidence lost outstanding peers or confused committed state.");
            selection.Selected = source; selection.NearestRejected = station; selection.Rejection = "chest_on_inward_side";
            Check(diagnostics.Begin(4, "selected_chest_open", station.m_uid, port.Kind, source.m_uid), "Selected-chest evidence was denied.");
            diagnostics.Evidence(4, "selected_chest_open", "smelter", station, port, source, new Quaternion(0, 0, 0, 1), selection, default, null, null, 0);
            Check(lines[^1].Contains("selected_distance_m=2.0") && lines[^1].Contains("rejected_distance_m=1.0") && lines[^1].Contains("rejected_reason=chest_on_inward_side") && !lines[^1].Contains(" placement_reason=") && !lines[^1].Contains(" candidate="), "Rejected candidate measurements were confused with the selected chest.");
            // The identity cache is bounded even across many different stations and windows.
            for (uint i = 0; i < 300; i++) diagnostics.Begin(120 + i * 61, "wait", new ZDOID(94, i + 1));
            var cache = (System.Collections.IDictionary)typeof(StationSupplyDiagnostics).GetField("_reasons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(diagnostics)!;
            Check(cache.Count == 256, "Diagnostic identity history is unbounded.");
        }
        finally { ZDOExtraData.Release(station, station.m_uid); ZDOExtraData.Release(source, source.m_uid); LogCapture.ResetVerbosity(); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: StationSupply preserves distinct station/intake identities, changed conditions, bounded history/volume and omission counts; Normal/Verbose logs retain placement/capacity evidence and honor debug policy.");
    }
    private static void Placement()
    {
        var origin = new Vector3(100, 20, 200); var identity = new Quaternion(0, 0, 0, 1);
        var highHopper = new Vector3(-1.6f, 4.3f, 0);
        Check(StationSupplyRules.Distance(origin, identity, highHopper, origin + new Vector3(-2.4f, 0, 0), 2, 1.5f).HasValue, "A floor-level chest below the high refinery input was rejected.");
        Check(!StationSupplyRules.Distance(origin, identity, highHopper, origin + new Vector3(1, 0, 0), 4, 1.5f).HasValue, "A chest behind the opposite side of the input was accepted.");
        Check(!StationSupplyRules.Distance(origin, identity, highHopper, origin + new Vector3(-2.4f, 2, 0), 2, 1.5f).HasValue, "A chest on the next floor was accepted.");
        var quarter = new Quaternion(0, (float)Math.Sqrt(.5), 0, (float)Math.Sqrt(.5));
        Check(StationSupplyRules.Distance(origin, quarter, new Vector3(2, 1, 0), origin + new Vector3(0, 0, -2.75f), 2, 1.5f).HasValue &&
            !StationSupplyRules.Distance(origin, quarter, new Vector3(2, 1, 0), origin + new Vector3(2.75f, 0, 0), 2, 1.5f).HasValue, "Placed station rotation did not rotate the input anchor.");
        Check(StationSupplyRules.Distance(Vector3.zero, identity, Vector3.zero, new Vector3(2, 0, 0), 2, 1.5f) == 4 &&
            !StationSupplyRules.Distance(Vector3.zero, identity, Vector3.zero, new Vector3(2.01f, 0, 0), 2, 1.5f).HasValue &&
            !StationSupplyRules.Distance(Vector3.zero, identity, Vector3.zero, new Vector3(float.NaN, 0, 0), 2, 1.5f).HasValue, "Range boundary or nonfinite placement is wrong.");
        Check(StationSupplyRules.Prefer(1, new ZDOID(1, 1), 2, new ZDOID(1, 2)) &&
            !StationSupplyRules.Prefer(2, new ZDOID(1, 1), 1, new ZDOID(1, 2)) &&
            StationSupplyRules.Prefer(1, new ZDOID(1, 1), 1, new ZDOID(1, 2)), "Nearest-source selection or stable tie-break changed.");
        Check(!StationSupplyRules.FuelRoom(float.NaN, 20) && !StationSupplyRules.FuelRoom(19.5f, 20) && StationSupplyRules.FuelRoom(19, 20) &&
            !StationSupplyRules.QueueRoom(-1, 10) && !StationSupplyRules.QueueRoom(10, 10) && StationSupplyRules.QueueRoom(9, 10), "Native capacity or corrupt queue guards failed.");
        System.Console.WriteLine("PASS: StationSupply intake geometry follows rotation, separates opposite sides/floors, accepts tall hopper floor placement and enforces deterministic distance/capacity boundaries.");
    }
    private static byte[] Item(int prefab, int stack, byte x, bool rich = false)
    {
        var p = new ZPackage(); p.Write(1234567); p.Write(x); p.Write((byte)0); p.Write((byte)0);
        p.Write((byte)(64 | (stack != 1 ? 8 : 0) | (rich ? 4 | 16 | 32 | 128 : 0)));
        if (rich) p.Write((ushort)3);
        if (stack != 1) p.Write((ushort)stack);
        if (rich) { p.Write(7); p.Write(987654321L); p.Write("Original crafter"); }
        p.Write(prefab);
        if (rich) { p.WriteNumItems(2); p.Write("custom:key"); p.Write("preserve=exactly"); p.Write("second"); p.Write("value"); }
        p.Write((byte)(rich ? 1 : 0)); return p.GetArray();
    }
    private static byte[] Contents(params byte[][] items)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(110); writer.Write((ushort)items.Length); foreach (var item in items) writer.Write(item);
        return stream.ToArray();
    }
    private static void Inventory()
    {
        var coal = Item(11, 2, 0, true); var ore = Item(22, 1, 1); var unknown = Item(999, 5, 2, true);
        var original = Contents(coal, ore, unknown); var copy = (byte[])original.Clone();
        var result = NativeInventory.WithdrawOne(original, 5, 2, new[] { 11 })!;
        Check(result.Prefab == 11 && result.Cheated && original.SequenceEqual(copy), "Withdrawal changed original bytes or lost cheated provenance.");
        var expectedCoal = (byte[])coal.Clone(); expectedCoal[10] = 1; expectedCoal[11] = 0;
        Check(result.Bytes.SequenceEqual(Contents(expectedCoal, ore, unknown)), "Stack decrement reserialized unrelated metadata or integer durability.");
        result = NativeInventory.WithdrawOne(original, 5, 2, new[] { 22, 11 })!;
        Check(result.Prefab == 22 && result.Bytes.SequenceEqual(Contents(coal, unknown)), "Native conversion preference, single-stack removal or untouched unknown items failed.");
        Check(NativeInventory.WithdrawOne(original, 5, 2, new[] { 123 }) == null &&
            NativeInventory.WithdrawOne(Contents(), 5, 2, new[] { 11 }) == null &&
            NativeInventory.WithdrawOne(Contents(coal, Item(22, 1, 0)), 5, 2, new[] { 11 }) == null &&
            NativeInventory.WithdrawOne(Contents(Item(11, 0, 0)), 5, 2, new[] { 11 }) == null &&
            NativeInventory.WithdrawOne(Contents(Item(11, 1, 6)), 5, 2, new[] { 11 }) == null, "Empty/wrong-resource/corrupt/duplicate-slot inventory was accepted.");
        for (int length = 0; length < original.Length; length++)
            Check(NativeInventory.WithdrawOne(original.Take(length).ToArray(), 5, 2, new[] { 11 }) == null, "Truncated native inventory accepted at " + length);
        Check(NativeInventory.WithdrawOne(original.Concat(new byte[] { 1 }).ToArray(), 5, 2, new[] { 11 }) == null, "Trailing inventory data accepted.");
        var last = NativeInventory.WithdrawOne(Contents(Item(11, 1, 0)), 5, 2, new[] { 11 })!;
        Check(last.Bytes.SequenceEqual(Contents()), "Last item was not consumed exactly once.");
        NativeInventory.CheckCompatibility();
        System.Console.WriteLine("PASS: StationSupply consumes one real item, preserves untouched native bytes/metadata, honors conversion order and rejects every truncated payload, invalid slot, duplicate and missing resource.");
    }
    private static void Transactions()
    {
        var netField = typeof(ZNet).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var worldField = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousNet = netField.GetValue(null); var previousWorld = worldField.GetValue(null);
        netField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZNet)));
        worldField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan)));
        var station = Ward(11, Vector3.zero); var chest = Ward(12, Vector3.zero);
        try
        {
            var original = Contents(Item(11, 3, 0, true));
            var withdrawal = NativeInventory.WithdrawOne(original, 5, 2, new[] { 11 })!;
            foreach (var kind in new[] { SupplyKind.Fuel, SupplyKind.Material, SupplyKind.Fermenter })
            {
                ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_fuel, 3f);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_queued, 0);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_content, 0);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_startTime, 42L);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_cheatedQueued, 0);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_health, 731f);
                var transaction = new SupplyTransaction { Station = new(station), Chest = new(chest), Intake = new SupplyIntake { Kind = kind, Capacity = 10 } };
                Check(StationSupplyTransfer.TryCommit(transaction, original, withdrawal, "resource", 56), "A valid transfer was rejected.");
                Check(chest.GetByteArray(ZDOVars.s_items).SequenceEqual(withdrawal.Bytes) && station.GetFloat(ZDOVars.s_health) == 731, "Transfer lost inventory or changed unrelated station health.");
                Check(kind == SupplyKind.Fuel ? station.GetFloat(ZDOVars.s_fuel) == 4 :
                    kind == SupplyKind.Material ? station.GetInt(ZDOVars.s_queued) == 1 && station.GetString("item0") == "resource" && station.GetBool(ZDOVars.s_cheatedQueued) :
                    station.GetInt(ZDOVars.s_content) == 11 && station.GetLong(ZDOVars.s_startTime) == 56 && station.GetBool(ZDOVars.s_cheatedQueued), "Destination addition did not match its native resource slot.");
                Check(!StationSupplyTransfer.TryCommit(transaction, original, withdrawal, "resource", 56, out var commitReason) && commitReason == "source_inventory_changed", "Retry spent the same captured inventory twice or lost its source-change reason.");
                // Inject an error after both records change; rollback must restore both.
                ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_fuel, 3f);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_queued, 0);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_content, 0);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_startTime, 42L);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_cheatedQueued, 0);
                int reloads = 0; bool failed = false;
                try { StationSupplyTransfer.TryCommit(transaction, original, withdrawal, "resource", 56, () => { if (++reloads == 1) throw new IOException("injected reload failure"); }); }
                catch (IOException) { failed = true; }
                Check(failed && reloads == 2 && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original) &&
                    station.GetFloat(ZDOVars.s_fuel) == 3 && station.GetInt(ZDOVars.s_queued) == 0 && station.GetInt(ZDOVars.s_content) == 0 &&
                    station.GetLong(ZDOVars.s_startTime) == 42 && !station.GetBool(ZDOVars.s_cheatedQueued), "Synchronous failure did not restore source and destination.");
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_fuel, 10f);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_queued, 10);
                ZDOExtraData.Set(station.m_uid, ZDOVars.s_content, 999);
                Check(!StationSupplyTransfer.TryCommit(transaction, original, withdrawal, "resource", 56, out commitReason) && commitReason == "station_capacity_changed" && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original), "A full station consumed a source item or lost its capacity-change reason.");
            }
        }
        finally
        {
            ZDOExtraData.Release(station, station.m_uid); ZDOExtraData.Release(chest, chest.m_uid);
            netField.SetValue(null, previousNet); worldField.SetValue(null, previousWorld);
        }
        System.Console.WriteLine("PASS: actual native source/destination records conserve one item for every intake kind, reject replay/full capacity and restore both records after an injected post-write failure.");
    }
    private static void DisposalInventory()
    {
        var source = Contents(Item(11, 1, 0), Item(22, 3, 1, true), Item(33, 2, 2));
        var captured = (byte[])source.Clone();
        var withdrawal = NativeInventory.WithdrawOne(source, 5, 2, Array.Empty<int>(), id => id == 22 || id == 33)!;
        Check(withdrawal.Prefab == 22 && source.SequenceEqual(captured), "Category filtering did not skip unselected entries in the same chest or changed original bytes.");
        Check(NativeInventory.WithdrawOne(source, 5, 2, Array.Empty<int>(), _ => false) == null, "No selected category still withdrew an item.");
        var original = Contents(Item(22, 4, 0, true));
        var inserted = NativeInventory.InsertOne(original, 7, 3, withdrawal, 50)!;
        Check(inserted.SequenceEqual(Contents(Item(22, 5, 0, true))), "Destination merge changed metadata or consumed the wrong count.");
        Check(NativeInventory.InsertOne(Contents(Item(22, 1, 0, true)), 7, 3, withdrawal, 50)!.SequenceEqual(Contents(Item(22, 2, 0, true))), "One-to-two stack encoding was wrong.");
        Check(NativeInventory.InsertOne(NativeInventory.Empty(), 7, 3, withdrawal, 50)!.SequenceEqual(Contents(Item(22, 1, 0, true))), "Empty Obliterator did not receive one intact item.");
        var unknown = Item(999, 5, 0, true);
        Check(NativeInventory.InsertOne(Contents(unknown), 7, 3, withdrawal, 50)!.SequenceEqual(Contents(unknown, Item(22, 1, 1, true))), "Unknown destination content was altered or the first free slot was wrong.");
        Check(NativeInventory.InsertOne(Contents(Item(22, 4, 0)), 7, 3, withdrawal, 50)!.SequenceEqual(Contents(Item(22, 4, 0), Item(22, 1, 1, true))), "Different item metadata was merged and lost.");
        Check(NativeInventory.InsertOne(Contents(Item(22, 50, 0, true)), 1, 1, withdrawal, 50) == null &&
            NativeInventory.InsertOne(Contents(Item(999, 1, 0)), 1, 1, withdrawal, 50) == null, "A full Obliterator accepted another item.");
        for (int length = 0; length < original.Length; length++)
            Check(NativeInventory.InsertOne(original.Take(length).ToArray(), 7, 3, withdrawal, 50) == null, "Truncated destination inventory was accepted at " + length);
        Check(NativeInventory.InsertOne(original.Concat(new byte[] { 1 }).ToArray(), 7, 3, withdrawal, 50) == null &&
            NativeInventory.InsertOne(Contents(Item(22, 1, 0), Item(999, 1, 0)), 7, 3, withdrawal, 50) == null, "Trailing destination data or duplicate slots were accepted.");
        System.Console.WriteLine("PASS: Obliterator insertion preserves exact item metadata, merges only equivalent stacks within capacity, skips unselected source categories and rejects corrupt/full destinations.");
    }
    private static void DisposalTransactions()
    {
        var netField = typeof(ZNet).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var worldField = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousNet = netField.GetValue(null); var previousWorld = worldField.GetValue(null);
        netField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZNet)));
        worldField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan)));
        var station = Ward(21, Vector3.zero); var chest = Ward(22, Vector3.zero);
        try
        {
            var original = Contents(Item(11, 3, 0, true)); var target = Contents(Item(999, 2, 0));
            var withdrawal = NativeInventory.WithdrawOne(original, 5, 2, Array.Empty<int>(), _ => true)!;
            var destination = NativeInventory.InsertOne(target, 7, 3, withdrawal, 50)!;
            var transaction = new SupplyTransaction { Station = new(station), Chest = new(chest), Intake = new SupplyIntake { Kind = SupplyKind.Obliterator } };
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); ZDOExtraData.Set(station.m_uid, ZDOVars.s_items, target);
            Check(StationSupplyContainerTransfer.TryCommit(transaction, original, target, withdrawal, destination) &&
                chest.GetByteArray(ZDOVars.s_items).SequenceEqual(withdrawal.Bytes) && station.GetByteArray(ZDOVars.s_items).SequenceEqual(destination), "Container-to-container transfer did not conserve one item.");
            Check(!StationSupplyContainerTransfer.TryCommit(transaction, original, target, withdrawal, destination, out var commitReason) && commitReason == "source_inventory_changed", "A repeated disposal commit spent the captured source twice or lost its source-change reason.");
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); ZDOExtraData.Set(station.m_uid, ZDOVars.s_items, target);
            int reloads = 0; bool failed = false;
            try { StationSupplyContainerTransfer.TryCommit(transaction, original, target, withdrawal, destination, reloadDestination: () => { if (++reloads == 1) throw new IOException("injected destination failure"); }); }
            catch (IOException) { failed = true; }
            Check(failed && reloads == 2 && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original) && station.GetByteArray(ZDOVars.s_items).SequenceEqual(target), "A failure did not roll back both disposal inventories.");
            Check(!StationSupplyContainerTransfer.TryCommit(transaction, original, NativeInventory.Empty(), withdrawal, destination, out commitReason) && commitReason == "destination_inventory_changed" && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original), "A concurrently changed destination consumed source stock or lost its destination-change reason.");
        }
        finally
        {
            ZDOExtraData.Release(station, station.m_uid); ZDOExtraData.Release(chest, chest.m_uid);
            netField.SetValue(null, previousNet); worldField.SetValue(null, previousWorld);
        }
        System.Console.WriteLine("PASS: native disposal inventory commits conserve one item, reject replay/concurrent destination changes and restore both containers after an injected post-write failure.");
    }
    private static void Categories()
    {
        Check(StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Material) == ObliteratorCategories.Materials &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Trophy) == ObliteratorCategories.Trophies &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Fish) == ObliteratorCategories.FoodAndMead &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Consumable) == ObliteratorCategories.FoodAndMead &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Shield) == ObliteratorCategories.Armor &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Tool) == ObliteratorCategories.Tools &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.AmmoNonEquipable) == ObliteratorCategories.Ammo &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Utility) == ObliteratorCategories.Other, "Item categories misclassified normal items.");
        Check(StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.Material, Skills.SkillType.None, true) == ObliteratorCategories.FoodAndMead &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.TwoHandedWeapon, Skills.SkillType.Pickaxes, false) == ObliteratorCategories.Tools &&
            StationSupplyItemCategories.Category(ItemDrop.ItemData.ItemType.OneHandedWeapon, Skills.SkillType.Axes, false) == (ObliteratorCategories.Weapons | ObliteratorCategories.Tools), "Cooking inputs, pickaxes or dual-purpose axes used confusing native-only categories.");
        foreach (var type in Enum.GetValues<ItemDrop.ItemData.ItemType>())
            Check(StationSupplyItemCategories.Allowed(type, ObliteratorCategories.All) && !StationSupplyItemCategories.Allowed(type, ObliteratorCategories.None), "All/None did not cover every native item type.");
        Check(StationSupplyItemCategories.Allowed(ItemDrop.ItemData.ItemType.Trophy, ObliteratorCategories.Materials | ObliteratorCategories.Trophies) &&
            !StationSupplyItemCategories.Allowed(ItemDrop.ItemData.ItemType.OneHandedWeapon, ObliteratorCategories.Materials | ObliteratorCategories.Trophies), "Combined categories accepted unwanted equipment.");
        System.Console.WriteLine("PASS: friendly categories cover every native item type, honor All/None/combinations and classify cooking inputs, pickaxes and axes without item-name lists.");
    }
    private static void LeverQuiet()
    {
        var gate = new StationSupplyObliterator(0, 10); var a = new ZDOID(49, 1); var b = new ZDOID(49, 2);
        Check(!gate.Quiet(a, 9.9f) && gate.Quiet(a, 10), "Initial Obliterator quiet period was skipped.");
        gate.Observe(a, "RPC_RequestIncinerate".GetStableHashCode(), 12);
        Check(!gate.Quiet(a, 21.9f) && gate.Quiet(b, 13) && gate.Quiet(a, 22), "Lever activity did not defer only its source through the quiet boundary.");
        gate.Observe(a, "RPC_AnimateLeverReturn".GetStableHashCode(), 25);
        Check(!gate.Quiet(a, 34.9f) && gate.Quiet(a, 35), "Lever-return/cooldown activity was ignored.");
        for (uint i = 10; i < 267; i++) gate.Observe(new ZDOID(49, i), "RPC_AnimateLever".GetStableHashCode(), 40);
        Check(!gate.Quiet(b, 49.9f) && gate.Quiet(b, 50), "Signal overflow forgot activity without a conservative global pause.");
        Check(!gate.Quiet(a, -1) && gate.Quiet(a, 9), "Clock rollback prematurely allowed or permanently disabled feeding.");
        Check(!StationSupplyObliterator.LeverMethod("RPC_AddFuel".GetStableHashCode()), "An ordinary fuel input was treated as a lever.");
        System.Console.WriteLine("PASS: observed lever activity and startup defer Obliterator feeding, bounded overflow stays conservative and clock rollback cannot grant premature access.");
    }
    private static ZDO Ward(uint id, Vector3 position)
    {
        var z = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); z.m_uid = new ZDOID(729331, id);
        typeof(ZDO).GetField("m_prefab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(z, 77);
        typeof(ZDO).GetField("m_position", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(z, position);
        ZDOExtraData.Set(z.m_uid, ZDOVars.s_enabled, 1); ZDOExtraData.Set(z.m_uid, ZDOVars.s_creator, 100L);
        return z;
    }
    private static void Access()
    {
        var ward = Ward(1, Vector3.zero); var records = new[] { ward };
        var wards = PrefabRegistry.Current.Catalog<float>("StationSupply.ward-checks"); wards.Add(77, 32);
        Check(StationSupplyAccess.Allowed(records, wards, 100, Vector3.zero, Vector3.zero), "Ward creator was denied.");
        Check(!StationSupplyAccess.Allowed(records, wards, 200, Vector3.zero, Vector3.zero), "Unpermitted creator accessed warded source.");
        ZDOExtraData.Set(ward.m_uid, ZDOVars.s_permitted, 1); ZDOExtraData.Set(ward.m_uid, "pu_id0".GetStableHashCode(), 200L);
        Check(StationSupplyAccess.Allowed(records, wards, 200, Vector3.zero, Vector3.zero), "Native permitted creator was denied.");
        Check(StationSupplyAccess.Allowed(records, wards, 300, new Vector3(100, 0, 0), new Vector3(105, 0, 0)) &&
            !StationSupplyAccess.Allowed(records, wards, 300, new Vector3(100, 0, 0), Vector3.zero), "Ward source and station boundaries were conflated.");
        ZDOExtraData.Set(ward.m_uid, ZDOVars.s_enabled, 0);
        Check(StationSupplyAccess.Allowed(records, wards, 300, Vector3.zero, Vector3.zero), "Disabled ward still blocks shared supply.");
        Check(StationSupplyRules.NativeScale(ward), "Default native scale was rejected.");
        ZDOExtraData.Set(ward.m_uid, ZDOVars.s_scaleScalarHash, 2f);
        Check(!StationSupplyRules.NativeScale(ward), "Scaled station/chest was accepted with an incorrect intake anchor.");
        ZDOExtraData.Release(ward, ward.m_uid);
        System.Console.WriteLine("PASS: StationSupply respects native ward creator/permissions at both source and destination while allowing public shared storage outside wards.");
    }
    private static void Consumers()
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var list = (List<ObjectTransferAuthority>)typeof(ObjectTransferAuthority).GetField("Consumers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(list.Count == 0, "A prior ownership consumer leaked into the checks.");
        var first = (ObjectTransferAuthority)RuntimeHelpers.GetUninitializedObject(typeof(ObjectTransferAuthority));
        var second = (ObjectTransferAuthority)RuntimeHelpers.GetUninitializedObject(typeof(ObjectTransferAuthority));
        var a = new SupplyLease((ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO))) { Id = new ZDOID(9, 1) };
        var b = new SupplyLease((ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO))) { Id = new ZDOID(9, 2) };
        typeof(ObjectTransferAuthority).GetField("_leases", fields)!.SetValue(first, new Dictionary<ZDOID, OwnershipLease> { [a.Id] = a });
        typeof(ObjectTransferAuthority).GetField("_leases", fields)!.SetValue(second, new Dictionary<ZDOID, OwnershipLease> { [b.Id] = b });
        typeof(ObjectTransferAuthority).GetField("_staticObjects", fields)!.SetValue(second, true);
        var lookup = NativeAccess.Method<Func<ZDOID, ObjectTransferAuthority?>>(typeof(ObjectTransferAuthority), "For", typeof(ZDOID));
        try
        {
            list.Add(first); list.Add(second);
            Check(ReferenceEquals(lookup(a.Id), first) && ReferenceEquals(lookup(b.Id), second) && lookup(new ZDOID(9, 3)) == null, "Shared native dispatch mixed independent consumers.");
            var pause = NativeAccess.Method<Func<Component, bool>>(typeof(ObjectTransferAuthority), "Pause", typeof(Component));
            list.Remove(first);
            Check(ReferenceEquals(lookup(b.Id), second) && lookup(a.Id) == null && b.StaticControl && !b.FollowControl, "Removing one consumer disabled another, or static leases became animal following.");
            Check(pause(null!), "Static station work caused animal hooks to access a component.");
            ((Dictionary<ZDOID, OwnershipLease>)typeof(ObjectTransferAuthority).GetField("_leases", fields)!.GetValue(second)!).Clear();
            Check(pause(null!), "An active but empty consumer accessed native components.");
        }
        finally { list.Clear(); }
        System.Console.WriteLine("PASS: the shared ownership dispatcher isolates each consumer and preserves the other consumer when one is removed.");
    }
    private static void BatchInventory()
    {
        var original = Contents(Item(11, 3, 0, true), Item(22, 2, 1), Item(11, 2, 2), Item(999, 1, 3));
        var preferred = new[] { 22, 11 };
        for (int maximum = 1; maximum <= 10; maximum++)
        {
            var batch = NativeInventory.WithdrawMany(original, 5, 2, preferred, maximum, out var evidence)!;
            byte[] repeated = original; int moved = 0;
            while (moved < maximum)
            {
                var one = NativeInventory.WithdrawOne(repeated, 5, 2, preferred);
                if (one == null) break;
                var actual = batch.Items[moved++];
                Check(actual.Prefab == one.Prefab && actual.Cheated == one.Cheated && actual.OriginalStack == one.OriginalStack && actual.ItemBytes.SequenceEqual(one.ItemBytes), "Batch selection diverged from native one-unit preference.");
                repeated = one.Bytes;
            }
            Check(batch.Items.Count == moved && moved == Math.Min(maximum, 7) && batch.Bytes.SequenceEqual(repeated) && evidence.Stacks == 4 && evidence.Rejected == 1,
                "Batch bound, exhausted input, source bytes or single-parse evidence differed.");
        }
        int accepted = 0;
        var room = NativeInventory.WithdrawMany(original, 5, 2, preferred, 10, out _, room: _ => accepted++ < 3)!;
        Check(room.Items.Count == 3 && room.Bytes.SequenceEqual(NativeInventory.WithdrawMany(original, 5, 2, preferred, 3, out _)!.Bytes), "Capacity callback consumed the first unit that did not fit.");
        Check(StationSupplyRules.VisitRoom(SupplyKind.Fuel, 8.5f, 0, 0, 10, 10) == 1 &&
            StationSupplyRules.VisitRoom(SupplyKind.Material, 0, 8, 0, 10, 10) == 2 &&
            StationSupplyRules.VisitRoom(SupplyKind.Fermenter, 0, 0, 0, 10, 10) == 1,
            "Fractional fuel room, remaining material slots or single fermenter input changed.");
        var all = NativeInventory.WithdrawMany(original, 5, 2, Array.Empty<int>(), 10, out _, _ => true)!;
        var destination = Contents(Item(22, 1, 0), Item(11, 1, 1, true));
        var maximumStacks = Enumerable.Repeat(3, all.Items.Count).ToArray();
        var inserted = NativeInventory.InsertMany(destination, 3, 2, all.Items, maximumStacks, out int count, out _)!;
        byte[] singles = destination;
        for (int n = 0; n < all.Items.Count; n++)
        {
            var next = NativeInventory.InsertOne(singles, 3, 2, all.Items[n], 3);
            if (next == null) { Check(count == n, "Insertion did not stop at the first item that did not fit."); break; }
            singles = next;
        }
        Check(inserted.SequenceEqual(singles), "Batch insertion changed byte-equivalence, stack encoding or free-slot order.");
        var limited = NativeInventory.WithdrawMany(original, 5, 2, preferred, 10, out _)!;
        var partial = NativeInventory.InsertMany(Contents(Item(22, 2, 0)), 1, 1, limited.Items,
            Enumerable.Repeat(3, limited.Items.Count).ToArray(), out count, out _);
        Check(partial != null && count == 1, "Destination room did not limit a staged batch.");
        limited.Limit(count);
        Check(limited.Items.Count == 1 && limited.Bytes.SequenceEqual(NativeInventory.WithdrawOne(original, 5, 2, preferred)!.Bytes), "Uninserted items were not returned to the staged source.");
        for (int length = 0; length < original.Length; length++)
            Check(NativeInventory.WithdrawMany(original.Take(length).ToArray(), 5, 2, preferred, 10, out _) == null, "A truncated batch source was accepted.");
        Check(NativeInventory.WithdrawMany(Contents(Item(11, 2, 0), Item(22, 2, 0)), 5, 2, preferred, 10, out _) == null &&
            NativeInventory.WithdrawMany(original.Concat(new byte[] { 1 }).ToArray(), 5, 2, preferred, 10, out _) == null,
            "Duplicate slots or trailing batch inventory bytes were accepted.");
        var wrongFormat = (byte[])original.Clone(); wrongFormat[0] = 109;
        Check(NativeInventory.WithdrawMany(wrongFormat, 5, 2, preferred, 10, out var bad) == null && bad.Status == "unsupported_inventory_format", "Batch format validation changed.");
        System.Console.WriteLine("PASS: batched inventories match repeated singles, preserve item bytes and native order, stop at visit/room/input bounds and reject malformed sources.");
    }
    private static void BatchTransactions()
    {
        var netField = typeof(ZNet).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousNet = netField.GetValue(null); netField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZNet)));
        using var world = new ReviewRegressionTests.World(); var station = world.Add(19001); var chest = world.Add(19002);
        try
        {
            var original = Contents(Item(11, 2, 0, true), Item(22, 2, 1));
            var batch = NativeInventory.WithdrawMany(original, 5, 2, new[] { 22, 11 }, 4, out _)!;
            var names = new[] { "ore", "ore", "coal", "coal" };
            var t = new SupplyTransaction { Station = new(station), Chest = new(chest), Intake = new SupplyIntake { Kind = SupplyKind.Material, Capacity = 10 } };
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); ZDOExtraData.Set(station.m_uid, ZDOVars.s_queued, 2);
            for (int n = 0; n < names.Length; n++) ZDOExtraData.Set(station.m_uid, ("item" + (n + 2)).GetStableHashCode(), "previous" + n);
            int reloads = 0; bool failed = false;
            try { StationSupplyTransfer.TryCommit(t, original, batch, names, 55, out _, () => { if (++reloads == 1) throw new IOException("injected batch reload"); }); }
            catch (IOException) { failed = true; }
            Check(failed && reloads == 2 && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original) && station.GetInt(ZDOVars.s_queued) == 2 && !station.GetBool(ZDOVars.s_cheatedQueued), "Batch rollback lost inventory, queue count or cheated provenance.");
            for (int n = 0; n < names.Length; n++) Check(station.GetString("item" + (n + 2)) == "previous" + n, "Rollback did not restore queue key " + n);
            Check(StationSupplyTransfer.TryCommit(t, original, batch, names, 55, out _) && station.GetInt(ZDOVars.s_queued) == 6 && station.GetBool(ZDOVars.s_cheatedQueued), "An atomic mixed-input batch did not commit or lost cheated provenance.");
            for (int n = 0; n < names.Length; n++) Check(station.GetString("item" + (n + 2)) == names[n], "Mixed queue order differed from withdrawal order.");
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); ZDOExtraData.Set(station.m_uid, ZDOVars.s_fuel, .33333334f); t.Intake.Kind = SupplyKind.Fuel;
            float expectedFuel = .33333334f; for (int n = 0; n < batch.Items.Count; n++) expectedFuel += 1f;
            Check(StationSupplyTransfer.TryCommit(t, original, batch, names, 55, out _) && station.GetFloat(ZDOVars.s_fuel) == expectedFuel,
                "Batched float fuel additions differed from native unit-at-a-time rounding.");
            t.Intake.Kind = SupplyKind.Material;
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); ZDOExtraData.Set(station.m_uid, ZDOVars.s_queued, 8);
            Check(!StationSupplyTransfer.TryCommit(t, original, batch, names, 55, out string reason) && reason == "station_capacity_changed" && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original), "A changed capacity partially consumed a batch.");
            t.Intake.Kind = SupplyKind.Obliterator;
            var target = Contents(Item(999, 2, 0)); ZDOExtraData.Set(station.m_uid, ZDOVars.s_items, target);
            var destination = NativeInventory.InsertMany(target, 7, 3, batch.Items, new[] { 50, 50, 50, 50 }, out _, out _)!;
            reloads = 0; failed = false;
            try { StationSupplyContainerTransfer.TryCommit(t, original, target, batch, destination, out _, reloadDestination: () => { if (++reloads == 1) throw new IOException("injected destination batch reload"); }); }
            catch (IOException) { failed = true; }
            Check(failed && reloads == 2 && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original) && station.GetByteArray(ZDOVars.s_items).SequenceEqual(target), "Batch failure did not restore both inventories.");
            Check(StationSupplyContainerTransfer.TryCommit(t, original, target, batch, destination, out _) && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(batch.Bytes), "A valid container batch did not consume exactly its items.");
            ZDOExtraData.Set(chest.m_uid, ZDOVars.s_items, original); station.RemoveByteArray(ZDOVars.s_items); reloads = 0; failed = false;
            try { StationSupplyContainerTransfer.TryCommit(t, original, null, batch, destination, out _, reloadDestination: () => { if (++reloads == 1) throw new IOException("absent destination"); Check(station.GetByteArray(ZDOVars.s_items).SequenceEqual(NativeInventory.Empty()), "Rollback did not reload an empty native destination."); }); }
            catch (IOException) { failed = true; }
            Check(failed && reloads == 2 && station.GetByteArray(ZDOVars.s_items) == null && chest.GetByteArray(ZDOVars.s_items).SequenceEqual(original), "Rollback did not restore an originally absent destination field.");
        }
        finally { ZDOExtraData.Release(station, station.m_uid); ZDOExtraData.Release(chest, chest.m_uid); netField.SetValue(null, previousNet); }
        System.Console.WriteLine("PASS: atomic batches preserve mixed queue order/cheated provenance, reject changed capacity and roll back every queue key and both inventories.");
    }
    private static void ActiveTransactions()
    {
        using var world = new ReviewRegressionTests.World();
        var a = new SupplyTransaction { Station = new(world.Add(19101)), Chest = new(world.Add(19102)), Intake = new SupplyIntake { Kind = SupplyKind.Material }, Committed = true, PendingRefresh = 9 };
        var b = new SupplyTransaction { Station = new(world.Add(19103)), Chest = new(world.Add(19104)), Intake = new SupplyIntake { Kind = SupplyKind.Fuel }, Committed = true, PendingRefresh = 9 };
        var active = new List<SupplyTransaction>(4) { a };
        Check(StationSupplyController.Disjoint(active, b.Station.Id, b.Chest.Id), "Separate station/chest records blocked concurrency.");
        foreach (var pair in new[] { (a.Station.Id, b.Chest.Id), (b.Station.Id, a.Chest.Id), (a.Chest.Id, b.Chest.Id), (b.Station.Id, a.Station.Id) })
            Check(!StationSupplyController.Disjoint(active, pair.Item1, pair.Item2), "Two transactions were allowed to share a record or cross roles.");
        using var log = new ManualLogSource("Supply concurrency checks");
        var settings = new StationSupplySettings(new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false }, log);
        var controller = (StationSupplyController)RuntimeHelpers.GetUninitializedObject(typeof(StationSupplyController));
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object value) => typeof(StationSupplyController).GetField(name, fields)!.SetValue(controller, value);
        Set("_settings", settings); Set("_diagnostics", new StationSupplyDiagnostics(log, settings)); Set("_counts", new SupplyCounts()); Set("_active", active);
        var advance = typeof(StationSupplyController).GetMethod("AdvanceSet", fields)!;
        settings.MaxActiveTransactions.Value = 1;
        advance.Invoke(controller, new object[] { 0f, 0 });
        Check(active.Count == 1 && a.PendingRefresh == 0, "Single-transaction mode changed pending refresh progress.");
        settings.MaxActiveTransactions.Value = 2; active.Add(b); a.PendingRefresh = 9;
        advance.Invoke(controller, new object[] { .1f, 0 });
        Check(active.Count == 2 && a.PendingRefresh == 0 && b.PendingRefresh == 0, "One set pass did not advance both independent transactions.");
        System.Console.WriteLine("PASS: shared/cross-role records are excluded and both single/concurrent transaction sets advance their own refresh state in one pass.");
    }
    private static void ScenePasses()
    {
        var guard = new SceneListPass(); var near = new List<ZDO>(); var other = new List<ZDO>(); var world = new object();
        Check(guard.Enter(100, near, world) && !guard.Enter(100, near, world) && guard.Enter(101, near, world) &&
            guard.Enter(101, other, world) && !guard.Enter(101, other, world) && guard.Enter(101, other, new object()), "Scene extension did not isolate frame, list identity or world.");
        guard.Clear(); Check(guard.Enter(101, other, world), "Scene extension guard retained disposed state.");
        System.Console.WriteLine("PASS: scene extension skips the repeated same-list frame and re-enters for changed frames, list identities, worlds and disposal.");
    }
    private static void Settings()
    {
        using var log = new ManualLogSource("StationSupply checks");
        string path = Path.Combine(Path.GetTempPath(), "heimkit-supply-" + Guid.NewGuid() + ".cfg");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        var settings = new StationSupplySettings(config, log);
        Check(settings.Enabled.Value && !settings.Fermenters.Value && !settings.Obliterators.Value && settings.ObliteratorCategories.Value == ObliteratorCategories.All && settings.ChestRangeMeters.Value == 2 && settings.ChestHeightToleranceMeters.Value == 1.5f && !config.ContainsKey(new ConfigDefinition("StationSupply", "DisableDebugLogging")), "Supply defaults or hidden logging policy are wrong.");
        Check(settings.MaxItemsPerVisit.Value == 10 && settings.MaxActiveTransactions.Value == 2, "Batch/concurrency defaults changed.");
        settings.MaxItemsPerVisit.Value = 100; settings.MaxActiveTransactions.Value = 20;
        Check(settings.MaxItemsPerVisit.Value == 64 && settings.MaxActiveTransactions.Value == 4, "Batch/concurrency upper bounds were not enforced.");
        settings.MaxItemsPerVisit.Value = 0; settings.MaxActiveTransactions.Value = 0;
        Check(settings.MaxItemsPerVisit.Value == 1 && settings.MaxActiveTransactions.Value == 1, "Single-item/single-transaction lower bounds changed.");
        settings.ObliteratorCategories.SetSerializedValue("Materials, Trophies");
        Check(settings.ObliteratorCategories.Value == (ObliteratorCategories.Materials | ObliteratorCategories.Trophies) &&
            settings.ObliteratorCategories.GetSerializedValue() == "Materials, Trophies", "Category combinations did not round-trip through the real configuration converter.");
        settings.ObliteratorCategories.SetSerializedValue("All");
        Check(settings.ObliteratorCategories.Value == ObliteratorCategories.All && !settings.Obliterators.Value, "Restoring All enabled disposal feeding or lost category selection.");
        System.Console.WriteLine("PASS: StationSupply binds independent defaults, optional fermenter/Obliterator controls, category combinations and manual-only debug suppression.");
    }
}
