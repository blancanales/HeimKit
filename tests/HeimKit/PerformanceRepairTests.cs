using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.StationSupply;
using HeimKit.Features.StationSupply.Models;
using UnityEngine;
using TestConsole = System.Console;

internal static class PerformanceRepairTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { TerrainAdmission(); TransportAndPreflight(); RetryChanges(); PairedRefresh(); CooperativeFairness(); OutputBounds(); DisjointDiscovery(); }
    private sealed class Presence : IPlayerPresence
    {
        internal readonly List<ZNetPeer> Connected = new();
        public bool? HasConnectedPlayers => Connected.Count > 0;
        public bool? HasJoinedPlayers => HasConnectedPlayers;
        public int? ConnectedCount => Connected.Count;
        public int? ReadyCount => Connected.Count;
        public int? JoinedCount => Connected.Count;
        public IReadOnlyList<ZNetPeer> Peers => Connected;
        public void Refresh() { }
        public void Clear() => Connected.Clear();
    }
    private static void TerrainAdmission()
    {
        TerrainContext.CheckCompatibility();
        using var world = new ReviewRegressionTests.World();
        var source = new List<ZDO>(); var pending = new HashSet<ZDOID>(); var buffer = new List<ZDO>();
        for (int region = 0; region < 4; region++)
        {
            source.Add(world.Add((uint)(region * 2 + 1), region * 12, region * 12));
            var compiler = world.Add((uint)(region * 2 + 2), region * 12, region * 12); source.Add(compiler); pending.Add(compiler.m_uid);
        }
        Func<ZDO, bool> ready = record => !pending.Contains(record.m_uid);
        var filtered = TerrainContext.FilterCreation(source, buffer, ready);
        Check(filtered.Count == 4 && source.Count == 8 && world.Records.Count == 8, "Unready terrain reached creation or changed retained/persistent records.");
        source[1].Created = true;
        filtered = TerrainContext.FilterCreation(source, buffer, ready);
        Check(filtered.Count == 5 && filtered.Contains(source[1]), "Creation admission removed a live retained instance.");
        pending.Remove(source[3].m_uid);
        Check(TerrainContext.FilterCreation(source, buffer, ready).Contains(source[3]), "Heightmap readiness did not resume creation.");
        pending.Clear(); Check(ReferenceEquals(TerrainContext.FilterCreation(source, buffer, ready), source), "Ready coverage needlessly replaced native lists.");
        for (int i = 0; i < 20; i++) TerrainContext.FilterCreation(source, buffer, ready);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) TerrainContext.FilterCreation(source, buffer, ready);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes, "Warmed terrain admission allocated per native loop.");
        var compilerInstance = (TerrainComp)RuntimeHelpers.GetUninitializedObject(typeof(TerrainComp));
        Check(!TerrainContext.CompilerInitialized(compilerInstance), "A compiler from native Awake's early return was considered initialized.");
        var faultField = typeof(TerrainContext).GetField("_creationFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fault = new InvalidOperationException("creation admission fault");
        faultField.SetValue(null, fault);
        try
        {
            var admit = NativeAccess.Method<Func<List<ZDO>, List<ZDO>, List<ZDO>>>(typeof(TerrainContext), "AdmitCreation", typeof(List<ZDO>), typeof(List<ZDO>));
            Check(admit(source, buffer).Count == 0 && source.Count == 8 && world.Records.Count == 8, "A failed admission changed retained/persistent records.");
            bool reported = false;
            try { TerrainContext.ThrowIfFailed(); } catch (InvalidOperationException error) { reported = ReferenceEquals(error.InnerException, fault); }
            Check(reported, "A native callback failure was swallowed instead of reaching shared/owner isolation with its cause.");
        }
        finally { faultField.SetValue(null, null); }
        TestConsole.WriteLine("PASS: terrain creation defers unready compilers across four disjoint regions without destroying records or shrinking retention, resumes on readiness and allocates no warmed loop buffers.");
    }
    private static ZNetPeer Peer(long id, out ZPlayFabSocket socket)
    {
        socket = (ZPlayFabSocket)RuntimeHelpers.GetUninitializedObject(typeof(ZPlayFabSocket));
        var rpc = (ZRpc)RuntimeHelpers.GetUninitializedObject(typeof(ZRpc)); typeof(ZRpc).GetField("m_socket", Fields)!.SetValue(rpc, socket);
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = id; peer.m_rpc = rpc;
        SetState(socket, 3); return peer;
    }
    private static void SetState(ZPlayFabSocket socket, int state)
    {
        var field = typeof(ZPlayFabSocket).GetField("m_state", Fields)!; field.SetValue(socket, Enum.ToObject(field.FieldType, state));
    }
    private static void TransportAndPreflight()
    {
        NativeConnection.CheckCompatibility();
        using var world = new ReviewRegressionTests.World(); var station = world.Add(31); var chest = world.Add(32);
        var peer = Peer(81, out var socket); var presence = new Presence(); presence.Connected.Add(peer);
        var authority = (ObjectTransferAuthority)RuntimeHelpers.GetUninitializedObject(typeof(ObjectTransferAuthority));
        typeof(ObjectTransferAuthority).GetField("_world", Fields)!.SetValue(authority, world.Manager);
        typeof(ObjectTransferAuthority).GetField("_presence", Fields)!.SetValue(authority, presence);
        typeof(ObjectTransferAuthority).GetField("_leases", Fields)!.SetValue(authority, new Dictionary<ZDOID, OwnershipLease>());
        typeof(ObjectTransferAuthority).GetField("_staticObjects", Fields)!.SetValue(authority, true);
        var waits = new Dictionary<ZDOID, (ZDO Record, ZNetPeer Peer)>();
        typeof(ObjectTransferAuthority).GetField("_sourceWaits", Fields)!.SetValue(authority, waits);
        station.DataRevision = 1; chest.DataRevision = 0;
        try
        {
            Check(!authority.Preflight(chest, out var reason) && reason == "object_revision_unavailable" && station.GetOwner() == 0,
                "Revision-zero preflight took ownership or lost its precise reason.");
            chest.DataRevision = 1; Check(authority.Preflight(chest, out _), "A newly ready source revision stayed blocked.");
            NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long))(chest, peer.m_uid);
            Check(authority.Preflight(chest, out _) && NativeConnection.CanSend(peer), "A connected native transport was rejected.");
            typeof(ZPlayFabSocket).GetField("m_partyResetTimeout", Fields)!.SetValue(socket, 20f);
            Check(socket.IsConnected() && peer.IsReady() && !NativeConnection.CanSend(peer) && !authority.Preflight(chest, out _) && !authority.CanSend(peer),
                "PlayFab's ready/connected handshake bypassed TX suspension.");
            ushort ownerRevision = chest.OwnerRevision; long owner = chest.GetOwner();
            Check(!authority.Acquire(new SupplyLease(chest), 1, out _) && chest.GetOwner() == owner && chest.OwnerRevision == ownerRevision,
                "Acquisition mutated ownership while the physical source could not send.");
            var lease = new SupplyLease(chest) { PreviousPeer = peer };
            authority.RequestSourceState(lease, 1);
            Check(chest.OwnerRevision == ownerRevision && authority.SendBarrier(peer, chest, 1) == 0,
                "Suspended source retries changed revisions or emitted an ordering request.");
            typeof(ZPlayFabSocket).GetField("m_partyResetTimeout", Fields)!.SetValue(socket, 0f);
            Check(authority.Preflight(chest, out _), "Native reconnect recovery did not resume readiness.");
            SetState(socket, 2); Check(socket.IsConnected() && !NativeConnection.CanSend(peer), "CONNECTING was treated as send-ready.");
            SetState(socket, 0); Check(!NativeConnection.CanSend(peer), "A closed native transport was accepted.");
            NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long))(chest, 0);
            waits[chest.m_uid] = (chest, peer);
            Check(!authority.Preflight(chest, out _), "Owner-zero cleanup erased an unresolved original-source obligation.");
            SetState(socket, 3); Check(authority.Preflight(chest, out _) && ReferenceEquals(authority.OwnerConnection(chest), peer), "A recovered original source was not retained for draining.");
            var replacement = Peer(81, out _); presence.Connected.Clear(); presence.Connected.Add(replacement);
            Check(!authority.CanSend(peer) && !authority.Preflight(chest, out _), "A replacement peer reused an old physical connection or skipped source draining.");
            NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long))(chest, replacement.m_uid);
            Check(authority.Preflight(chest, out _) && ReferenceEquals(authority.OwnerConnection(chest), replacement), "A new native owner could not become the new drain source.");
        }
        finally { ZDOExtraData.Release(station, station.m_uid); ZDOExtraData.Release(chest, chest.m_uid); }
        TestConsole.WriteLine("PASS: native disconnected, CONNECTING and TX-suspended peers fail preflight; revision readiness and reconnect recover, while physical connection replacement cannot inherit unresolved source state.");
    }
    private static void RetryChanges()
    {
        using var world = new ReviewRegressionTests.World(); var station = world.Add(51); var chest = world.Add(52);
        var port = new SupplyIntake(); var retry = new StationSupplyRetry();
        StationSupplyRetry.State State(bool ready = false) => new(station, chest, null, null, true, ready);
        var state = State(); retry.Failed(station.m_uid, port, chest.m_uid, state, 0);
        for (int i = 0; i < 100; i++) Check(!retry.Ready(station.m_uid, port, chest.m_uid, state, 1, out _), "An unchanged failure bypassed backoff.");
        Check(retry.Ready(station.m_uid, port, chest.m_uid, state, 4, out _), "A bounded retry never resumed.");
        station.OwnerRevision++; retry.Failed(station.m_uid, port, chest.m_uid, State(), 4);
        Check(!retry.Ready(station.m_uid, port, chest.m_uid, State(), 11, out _) && retry.Ready(station.m_uid, port, chest.m_uid, State(), 12, out _), "Our handoff revisions reset escalating retry delay.");
        chest.DataRevision++;
        Check(retry.Ready(station.m_uid, port, chest.m_uid, State(), 5, out _), "A new native revision did not bypass stale backoff.");
        retry.Failed(station.m_uid, port, chest.m_uid, State(), 5);
        Check(retry.Ready(station.m_uid, port, chest.m_uid, State(true), 6, out _), "Transport readiness did not invalidate backoff.");
        for (uint i = 1; i <= 1000; i++) retry.Failed(new ZDOID(12, i), port, chest.m_uid, State(), 7);
        Check(retry.Count <= StationSupplyRetry.Capacity, "Retry history is unbounded.");
        retry.Failed(station.m_uid, port, chest.m_uid, State(), 100);
        for (int i = 0; i < 20; i++) retry.Failed(station.m_uid, port, chest.m_uid, State(), 100);
        Check(retry.Ready(station.m_uid, port, chest.m_uid, State(), 130, out _) && retry.Ready(station.m_uid, port, chest.m_uid, State(), 1, out _), "Retry delay exceeded its ceiling or survived clock rollback.");
        TestConsole.WriteLine("PASS: unchanged failures back off with a bounded ceiling; own handoff revisions preserve escalation, readiness/revision changes resume promptly, rollback recovers and history remains bounded.");
    }
    private static void PairedRefresh()
    {
        var station = new ZDOID(7, 1); var chest = new ZDOID(7, 2);
        foreach (bool fermenter in new[] { false, true })
        {
            var order = new List<string>();
            ZdoReplication.ResetClientPair(station, chest, fermenter, packet =>
            {
                packet.SetPos(0); Check(packet.ReadInt() == (fermenter ? 1 : 2), "Refresh destruction count changed.");
                if (!fermenter) Check(packet.ReadZDOID() == station, "Station reset lost its original identity.");
                Check(packet.ReadZDOID() == chest && packet.GetPos() == packet.Size(), "Chest reset lost its original identity or has trailing bytes.");
                order.Add("remove");
            }, () => order.Add("station"), () => order.Add("chest"));
            Check(order.SequenceEqual(new[] { "remove", "station", "chest" }), "Paired snapshots preceded native destruction or reversed order.");
        }
        bool sent = false;
        try { ZdoReplication.ResetClientPair(station, chest, false, _ => throw new IOException(), () => sent = true, () => sent = true); }
        catch (IOException) { }
        Check(!sent, "A failed destruction send continued with replacement snapshots.");
        TestConsole.WriteLine("PASS: one native paired destruction precedes both original-ID snapshots, preserves fermenter instances, and stops snapshot dispatch when removal fails.");
    }
    private static void CooperativeFairness()
    {
        long clock = 0; var scheduler = new ModuleScheduler(() => clock); var owners = new[] { new object(), new object(), new object(), new object() };
        // Register without waiting for startup phases; all become due together later.
        foreach (var owner in owners) scheduler.Due(owner, "discovery", 0, 1, cooperative: true);
        var cleanup = new object(); scheduler.Due(cleanup, "cleanup", 0, .2f);
        var served = new HashSet<object>();
        for (int frame = 0; frame < 4; frame++)
        {
            scheduler.BeginFrame();
            foreach (var owner in owners)
                if (scheduler.Due(owner, "discovery", 2 + frame * .1f, 1, cooperative: true))
                { served.Add(owner); clock += Stopwatch.Frequency / 100; }
            Check(scheduler.Due(cleanup, "cleanup", 10 + frame, .2f), "Cooperative admission deferred safety cleanup.");
        }
        Check(served.Count == 4 && scheduler.Deferrals > 0, "Disjoint due consumers starved behind a fixed module order.");
        scheduler.Clear(); scheduler.BeginFrame();
        clock += Stopwatch.Frequency / 100;
        var expired = new object(); Check(!scheduler.Due(expired, "wait", 0, 1, cooperative: true), "Exhausted budget admitted new work.");
        scheduler.Forget(expired); scheduler.BeginFrame();
        var next = new object(); scheduler.Due(next, "next", 0, 1, cooperative: true);
        Check(scheduler.Due(next, "next", 1, 1, cooperative: true), "Disposed work blocked the admission queue.");
        TestConsole.WriteLine("PASS: cooperative scheduling shares elapsed budget fairly across four due consumers without gating cleanup and removes cancelled work.");
    }
    private static void OutputBounds()
    {
        DiagnosticLog.StartSession(); using var log = new ManualLogSource("Performance repair output");
        var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var settings = new StationSupplySettings(config, log); var diagnostics = new StationSupplyDiagnostics(log, settings);
        try
        {
            for (int i = 0; i < 1000; i++) { diagnostics.Transferred(1); diagnostics.FlushTransfers(2); }
            Check(lines.Count == 0, "Per-item output remained synchronous.");
            diagnostics.FlushTransfers(61); diagnostics.FlushTransfers(62, force: true);
            Check(lines.Count == 1 && lines[0].Contains("1000 real resources"), "Grouped results were lost, repeated or debug-gated.");
            ServerPerformance.BeginUpdate(); ServerPerformance.OptionalOutput(Stopwatch.Frequency);
            DiagnosticLog.Write(log, "StationSupply", CoreLogTopics.Scope, "Suppressed optional output", level: LogLevel.Debug);
            DiagnosticLog.Write(log, "StationSupply", "Actionable native timeout", LogLevel.Warning);
            Check(lines.Count == 2 && lines[^1].Contains("timeout"), "Optional elapsed limit hid a warning or failed to suppress output.");
            ServerPerformance.BeginUpdate(); Check(DiagnosticLog.DebugAvailable(log, "StationSupply"), "Optional output did not resume on the next update.");
            var counter = new ServerPerformance.Counter();
            Parallel.For(0, 1000, _ => counter.Add(17));
            var sample = counter.Take(); Check(sample.Calls == 1000 && sample.Maximum > 0 && counter.Take().Calls == 0, "Listener timing lost concurrent writes or retained prior window counts.");
        }
        finally { DiagnosticLog.StartSession(); }
        TestConsole.WriteLine("PASS: successful transfers group independently of Debug; elapsed optional-output limits preserve warnings and recover next update; timing counters remain bounded and thread-safe.");
    }
    private static void DisjointDiscovery()
    {
        using var world = new ReviewRegressionTests.World(); var expected = new List<ZDO>();
        for (int region = 0; region < 4; region++) { expected.Add(world.Add((uint)(200 + region), region * 10)); world.Add((uint)(300 + region), region * 10); }
        foreach (var table in expected) typeof(ZDO).GetField("m_prefab", Fields)!.SetValue(table, 99);
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(), observe: false);
        using var tables = provider.Subscribe("tables", record => record.GetPrefab() == 99, ScanKind.Nearby);
        provider.BeginCycle(true); var actual = new List<ZDO>();
        for (int region = 0; region < 4; region++) tables.Query(new Vector2s(region * 10, 0), new SimulationDistance(0, 0, true), actual);
        Check(actual.Count == 4 && expected.All(actual.Contains), "Targeted table collection dropped a disjoint player's table or included unrelated objects.");
        var created = world.Add(400, 20); typeof(ZDO).GetField("m_prefab", Fields)!.SetValue(created, 99); provider.Enqueue(created.m_uid);
        actual.Clear(); tables.Query(new Vector2s(20, 0), new SimulationDistance(0, 0, true), actual);
        Check(actual.Count == 2 && actual.Contains(created), "Record changes did not update targeted tables.");
        TestConsole.WriteLine("PASS: targeted table discovery retains all four disjoint regions, excludes unrelated records and accepts new table changes.");
    }
}
