using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HeimKit.Core.Diagnostics;
using BepInEx.Logging;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Native;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.SharedMap;
using UnityEngine;

internal static class MultiplayerLagTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { SceneBindings(); SnapshotWorker(); StoreGenerations(); CombatEvidence(); CombatAdmission(); BattlePerformanceEvidence(); ActiveAreaBoundaries(); LargeTerrainValidation(); }
    private static void SceneBindings()
    {
        using var world = new ReviewRegressionTests.World();
        var bindings = new Dictionary<ZDO, ZNetView>();
        var zdo = NativeAccess.Field<ZNetView, ZDO>("m_zdo");
        for (uint i = 1; i <= 4; i++)
        {
            var record = world.Add(500 + i, (int)i * 10); record.DataRevision = 27; record.Created = true;
            var view = (ZNetView)RuntimeHelpers.GetUninitializedObject(typeof(ZNetView)); zdo(view) = record; bindings[record] = view;
            Check(SceneInstances.BindingMatches(record, view.GetZDO(), true), "A live original binding was rejected.");
            // This is the native state left by ResetZDO before the observed gameObject failure.
            view.ResetZDO();
            Check(!SceneInstances.BindingMatches(record, view.GetZDO(), true) && SceneInstances.RemoveBinding(bindings, record, view, false), "A half-removed view remained in the native binding map.");
            Check(world.Records.ContainsKey(record.m_uid) && record.DataRevision == 27 && !record.Created, "Binding repair changed or deleted original world data.");
            var replacement = (ZNetView)RuntimeHelpers.GetUninitializedObject(typeof(ZNetView)); zdo(replacement) = record; bindings[record] = replacement;
            Check(!SceneInstances.RemoveBinding(bindings, record, view, false) && ReferenceEquals(bindings[record], replacement), "A delayed stale-view notification removed a replacement view.");
            Check(!SceneInstances.RemoveBinding(bindings, record, replacement, true), "A healthy native binding was removed.");
            Check(!SceneInstances.BindingMatches(record, record, false), "A destroyed Unity view was accepted just because its managed ZDO reference remained.");
        }
        Check(bindings.Count == 4 && world.Records.Count == 4, "Disjoint scene repair lost healthy regions.");
        var completed = NativeAccess.Method<Func<Exception?, long, Exception?>>(typeof(SceneInstances), "Completed", typeof(Exception), typeof(long));
        var fault = new NullReferenceException("native failure");
        Check(ReferenceEquals(completed(fault, 0), fault), "The scene observer swallowed or replaced a native exception.");
        foreach (string name in new[] { "OnDestroy", "ResetZDO" }) NativeAccess.Target(typeof(ZNetView), name);
        NativeAccess.Target(typeof(ZNetScene), "AddInstance", typeof(ZDO), typeof(ZNetView));
        var holder = typeof(SceneInstances).GetNestedType("Accessors", BindingFlags.NonPublic)!;
        Check((holder.Attributes & TypeAttributes.BeforeFieldInit) == 0, "Scene APIs can resolve before a consuming lease exists.");
        RuntimeHelpers.RunClassConstructor(holder.TypeHandle);
        System.Console.WriteLine("PASS: half-reset/destroyed scene bindings across four regions preserve saved records and replacement views; native errors remain visible and exact scene lifecycle APIs resolve.");
    }
    private static void SnapshotWorker()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int writes = 0; byte observed = 0;
        var file = new SnapshotFile("unused", bytes => { started.Set(); release.Wait(); observed = bytes[0]; Interlocked.Increment(ref writes); });
        file.Queue(new byte[] { 7 }, 7);
        try
        {
            Check(started.Wait(5000) && !file.Poll() && file.Pending, "Snapshot polling blocked or acknowledged a pending write.");
            bool rejected = false; try { file.Queue(new byte[] { 9 }, 9); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "One file admitted overlapping writes.");
        }
        finally { release.Set(); }
        Check(file.Poll(wait: true) && file.CompletedGeneration == 7 && observed == 7 && writes == 1, "A completed snapshot acknowledged newer live state.");
        file.Queue(new byte[] { 9 }, 9); Check(file.Poll(wait: true) && file.CompletedGeneration == 9, "A later generation could not be flushed.");
        var failed = new SnapshotFile("unused", _ => throw new IOException("injected write failure")); failed.Queue(new byte[] { 1 }, 5);
        bool surfaced = false; try { failed.Poll(wait: true); } catch (IOException) { surfaced = true; }
        Check(surfaced && failed.CompletedGeneration == 0 && failed.Pending, "A failed write was acknowledged or lost its failure.");
        System.Console.WriteLine("PASS: snapshot workers never block ordinary polling, admit one immutable generation, join on flush, retain newer changes and surface write failures without acknowledging them.");
    }
    private static void StoreGenerations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-lag-stores-" + Guid.NewGuid().ToString("N"));
        try
        {
            object[] stores = { new SharedMapResourceStore(directory, 1), new SharedMapDungeonStore(directory, 2),
                new SharedMapMarkerRefresh(directory, 3), new SharedMapPinState(directory, 4), new SharedMapLandmarkStore(directory, 5, false) };
            foreach (var store in stores)
            {
                Type type = store.GetType(); string path = (string)type.GetProperty("FilePath")!.GetValue(store)!;
                var generation = type.GetField(type == typeof(SharedMapPinState) ? "<Revision>k__BackingField" : "_revision", Fields)!;
                var saved = type.GetField("_savedRevision", Fields)!;
                using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                int writes = 0; var disk = new SnapshotFile(path);
                var worker = new SnapshotFile(path, bytes => { if (Interlocked.Increment(ref writes) == 1) { started.Set(); release.Wait(); } disk.Write(bytes); });
                type.GetField("_snapshotFile", Fields)!.SetValue(store, worker); generation.SetValue(store, 1L);
                try
                {
                    bool completed = (bool)type.GetMethod("SaveIfDue")!.Invoke(store, new object[] { 0f, 60f })!;
                    Check(!completed && started.Wait(5000) && (long)saved.GetValue(store)! == 0, type.Name + " waited on disk or acknowledged a pending snapshot.");
                    generation.SetValue(store, 2L);
                }
                finally { release.Set(); }
                Check((bool)type.GetMethod("Save")!.Invoke(store, null)! && writes == 2 && (long)saved.GetValue(store)! == 2, type.Name + " lost mutations during its pending write or failed to join on lifecycle flush.");
                byte[] bytes = File.ReadAllBytes(path), previous = File.ReadAllBytes(path + ".bak");
                Check(bytes.Length > 32 && previous.Length == bytes.Length && SHA256.HashData(bytes[..^32]).SequenceEqual(bytes[^32..]), type.Name + " changed its checksum/backup format.");
                Check(!(bool)type.GetMethod("Save")!.Invoke(store, null)!, type.Name + " rewrote unchanged acknowledged state.");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: all five SharedMap stores queue immutable bytes without waiting, preserve mutations during writes, flush the latest generation, retain atomic backups/checksums and skip unchanged saves.");
    }
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
    private static void CombatEvidence()
    {
        var window = new CombatWindow(); window.Observe(10, false);
        Check(window.Active(17.9f) && !window.Active(18), "Ordinary combat pressure did not expire.");
        window.Observe(30, true); window.Observe(35, false);
        Check(window.Active(49) && window.Boss && !window.Active(50), "A normal signal shortened boss pressure or it never expired.");
        window.Observe(60, false); Check(!window.Active(59), "Clock rollback retained stale pressure.");
        window.SetRaid(true);
        Check(window.Active(10000) && !window.Boss, "A raid expired without a native end or retained a stale boss flag.");
        window.SetRaid(false); Check(!window.Active(10000), "The raid's end did not clear its independent pause.");
        window.SetRaid(true); window.Observe(10001, false); window.SetRaid(false);
        Check(window.Active(10008) && !window.Active(10009), "A raid ending erased still-current ordinary combat evidence.");
        NativeAccess.Target(typeof(RandEventSystem), "GetCurrentRandomEvent");
        Check(CombatActivity.Nearby(Vector3.zero, new Vector3(128, 0, 0)) && !CombatActivity.Nearby(Vector3.zero, new Vector3(129, 0, 0)) &&
            !CombatActivity.Nearby(Vector3.zero, new Vector3(float.NaN, 0, 0)), "Combat proximity accepted nonfinite or distant evidence.");
        using var log = new ManualLogSource("Combat evidence checks"); var peers = new Presence();
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 99; peer.m_characterID = new ZDOID(99, 1);
        peer.m_rpc = (ZRpc)RuntimeHelpers.GetUninitializedObject(typeof(ZRpc)); peers.Connected.Add(peer);
        using var messages = new PlayerRoutedMessages(); using var combat = new CombatActivity(peers, messages, log);
        var observe = (Action<ZRpc, ZPackage>)typeof(CombatActivity).GetMethod("Observe", Fields)!.CreateDelegate(typeof(Action<ZRpc, ZPackage>), combat);
        var pending = (System.Collections.ICollection)typeof(CombatActivity).GetField("_pending", Fields)!.GetValue(combat)!;
        for (uint i = 0; i < 200; i++)
        {
            var parameters = new ZPackage(); parameters.Write(1);
            var data = new ZRoutedRpc.RoutedRPCData { m_msgID = i, m_senderPeerID = 99, m_targetPeerID = 42,
                m_targetZDO = new ZDOID(10, i + 1), m_methodHash = "RPC_Damage".GetStableHashCode(), m_parameters = parameters };
            var packet = new ZPackage(); data.Serialize(packet); packet.SetPos(0);
            observe(peer.m_rpc, packet); Check(packet.GetPos() == 0, "Combat observation changed the native packet cursor.");
        }
        Check(pending.Count == 64, "Combat capture queue exceeded its bound."); combat.Dispose(); Check(pending.Count == 0, "Disposal retained combat evidence.");
        var spoof = new ZPackage(); spoof.Write(1L); spoof.Write(98L); spoof.Write(42L); spoof.Write(new ZDOID(10, 1)); spoof.Write("RPC_Damage".GetStableHashCode()); spoof.SetPos(0);
        observe(peer.m_rpc, spoof); observe(peer.m_rpc, new ZPackage()); Check(pending.Count == 0, "Spoofed or truncated combat headers were accepted.");
        System.Console.WriteLine("PASS: ordinary/boss combat windows renew and expire, rollback clears them, finite proximity is bounded, capture retains physical peer identity, rejects forged headers and preserves native packets with a 64-target ceiling.");
    }
    private static void CombatAdmission()
    {
        long clock = 0; var scheduler = new ModuleScheduler(() => clock); var jobs = new[] { new object(), new object(), new object() };
        foreach (var job in jobs) scheduler.Due(job, "work", 0, .01f, cooperative: true);
        var served = new HashSet<object>();
        for (int frame = 0; frame < 12; frame++)
        {
            float now = 1 + frame * .1f; scheduler.BeginFrame(now); clock += Stopwatch.Frequency / 100;
            foreach (var job in jobs) if (scheduler.Due(job, "work", now, .01f, cooperative: true)) served.Add(job);
        }
        Check(served.Count == 3, "Earlier mandatory work permanently starved cooperative jobs.");
        scheduler.Clear(); int admitted = 0;
        foreach (var job in jobs) scheduler.Due(job, "work", 0, .01f, cooperative: true);
        var urgent = new object(); scheduler.Due(urgent, "transaction", 0, .01f);
        scheduler.Combat.Observe(10, true);
        for (int frame = 0; frame < 20; frame++)
        {
            float now = 10 + frame * .1f; scheduler.BeginFrame(now);
            foreach (var job in jobs) if (scheduler.Due(job, "work", now, .01f, cooperative: true)) admitted++;
            Check(scheduler.Due(urgent, "transaction", now, .01f), "Combat pacing gated an active transaction or cleanup tick.");
        }
        Check(admitted == 0 && scheduler.CombatDeferrals > 0, "Combat admitted optional work through ordinary or starvation-recovery paths.");
        Check(!scheduler.Combat.Active(31), "Ordinary cadence could not resume after combat.");
        scheduler.Combat.SetRaid(true);
        for (int frame = 0; frame < 20; frame++)
        {
            float now = 1000 + frame; scheduler.BeginFrame(now); clock += Stopwatch.Frequency;
            foreach (var job in jobs) Check(!scheduler.Due(job, "work", now, .01f, cooperative: true), "A long raid admitted starved optional work.");
            Check(scheduler.Due(urgent, "transaction", now, .01f), "Raid gating stopped mandatory cleanup.");
        }
        scheduler.Combat.SetRaid(false); scheduler.BeginFrame(1020);
        int resumed = 0;
        foreach (var job in jobs) if (scheduler.Due(job, "work", 1020, .01f, cooperative: true)) resumed++;
        Check(resumed > 0 && resumed <= jobs.Length, "Cleared raid work never resumed or replayed missed intervals.");
        scheduler.Clear(); Check(!scheduler.Combat.Active(1020), "Disposal retained a raid pause.");
        System.Console.WriteLine("PASS: fights and long raids admit no optional work, including starvation recovery; mandatory transactions continue and quiet resumption avoids interval catch-up.");
    }
    private static void BattlePerformanceEvidence()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Battle performance evidence");
        var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debugMode = HeimKit.Configuration.DiagnosticsSettings.Bind(config);
        var verbosity = config.Bind("Diagnostics", "DebugVerbosity", HeimKit.Configuration.DebugVerbosity.Normal);
        verbosity.Value = HeimKit.Configuration.DebugVerbosity.Verbose;
        PerformanceStages.Record("SharedMap", "disabled_stage", 390);
        PerformanceStages.Report(log);
        debugMode.Value = true; verbosity.Value = HeimKit.Configuration.DebugVerbosity.Normal;
        PerformanceStages.Record("SharedMap", "normal_stage", 390);
        PerformanceStages.Report(log);
        Check(lines.Count == 0, "Performance stages emitted without global Verbose Debug.");
        verbosity.Value = HeimKit.Configuration.DebugVerbosity.Verbose;
        ModuleScheduler.Current.Combat.SetRaid(true);
        Check(!DiagnosticLog.DebugAvailable(log, "SharedMap"), "Optional output remained admitted during a raid.");
        PerformanceStages.Record("SharedMap", "raid_stage", 390);
        PerformanceStages.Report(log);
        Check(lines.Count == 0, "Slow-stage Debug output bypassed raid suppression.");
        ModuleScheduler.Current.Clear();
        PerformanceStages.Record("SharedMap", "table_decode", 390, 4194304, 12000, 1);
        PerformanceStages.Record("SharedMap", "table_decode", 4, 1, 1, 0);
        PerformanceStages.Report(log);
        Check(lines.Count == 1 && lines[0].Contains("SharedMap.table_decode calls=2 avg/max_ms=197.00/390.00") &&
            lines[0].Contains("max_sample_items=4194304 max_sample_bytes=12000 max_sample_gc0=1"),
            "Verbose Debug replaced the maximum sample's workload or carried evidence across a suppressed window.");
        PerformanceStages.Report(log); Check(lines.Count == 1, "An empty health window repeated prior evidence.");
        for (int i = 0; i < 10; i++) PerformanceStages.Record("HeimKit", "bounded_stage_" + i, 20 + i);
        PerformanceStages.Report(log);
        Check(lines.Count == 2 && lines[1].Split(" calls=").Length == 7 && !lines[1].Contains("max_sample_"),
            "Slow-stage evidence exceeded its six-stage bound or included unavailable measurements.");
        var policy = new HeimKit.Configuration.DebugLoggingPolicy(debugMode, false);
        var timing = new ProcessingTiming(log, "SharedMap", policy);
        timing.RegisterStages("decode", "pins");
        ServerPerformance.BeginUpdate(); ServerPerformance.OptionalOutput(Stopwatch.Frequency);
        using (timing.MeasureStage("decode")) { }
        using (timing.MeasureStage("pins")) { }
        using (timing.MeasureValue(0, () => 0)) { }
        Check(lines.Count == 2, "Exhausted optional output still emitted detailed stages.");
        ServerPerformance.BeginUpdate();
        using (timing.MeasureValue(61, () => 0)) { }
        Check(lines.Count == 3 && lines[2].Contains("decode calls=1") && lines[2].Contains("pins calls=1"),
            "Denied detailed output discarded a stage or required several independently admitted records.");
        ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession();
        debugMode.Value = false; verbosity.Value = HeimKit.Configuration.DebugVerbosity.Normal;
        System.Console.WriteLine("PASS: slow-stage evidence requires admitted Verbose Debug, preserves the maximum sample, omits unavailable measurements and clears each reporting window.");
    }
    private static void ActiveAreaBoundaries()
    {
        var one = new ActiveSceneArea(Vector3.zero, 64, 1, false);
        var classic = new ActiveSceneArea(Vector3.zero, 64, 2, true);
        var rounded = new ActiveSceneArea(Vector3.zero, 64, 2, false);
        Check(one.Contains(new Vector3(64, 9000, -64)) && !one.Contains(new Vector3(64.01f, 0, 0)), "One-zone inclusive edges or height independence changed.");
        Check(classic.Contains(new Vector3(96, 0, 96)) && !rounded.Contains(new Vector3(96, 0, 96)), "Classic square and modern rounded corners were conflated.");
        Check(rounded.Contains(new Vector3(96, 0, 0)) && rounded.Contains(new Vector3(79, 0, 79)) && !rounded.Contains(new Vector3(80, 0, 80)), "Modern radial/axis boundaries changed.");
        var remote = new ActiveSceneArea(new Vector3(-6400, 0, 6400), 64, 2, false);
        Check(remote.Contains(new Vector3(-6304, -100, 6400)) && !rounded.Contains(new Vector3(-6304, -100, 6400)) &&
            !rounded.Contains(new Vector3(float.NaN, 0, 0)), "Disjoint regions or nonfinite scene points changed coverage.");
        System.Console.WriteLine("PASS: captured active-scene geometry preserves native inclusive square edges, modern rounded corners, horizontal height semantics and disjoint regions.");
    }
    private static void LargeTerrainValidation()
    {
        const int size = 2048;
        byte[] raw = new byte[8 + size * size];
        BitConverter.GetBytes(1).CopyTo(raw, 0); BitConverter.GetBytes(size * size).CopyTo(raw, 4);
        raw[8] = raw[^1] = 1;
        byte[] Pack()
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(raw);
            return output.ToArray();
        }
        var decoded = SharedMapTableData.Decode(size, Pack());
        var exploration = new SharedMapExploration(size, 12, 50, .3f);
        SharedMapTableData.ImportValidated(exploration, decoded);
        Check(exploration.ExploredPixels == 2, "Large-table validation changed first/last terrain values.");
        raw[^1] = 2; bool rejected = false;
        try { SharedMapTableData.Decode(size, Pack()); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Bulk terrain validation skipped an invalid final boolean byte.");
        raw[^1] = 1; Array.Resize(ref raw, raw.Length + 1); rejected = false;
        try { SharedMapTableData.Decode(size, Pack()); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Bulk terrain validation accepted trailing native payload bytes.");
        System.Console.WriteLine("PASS: full-sized terrain decoding preserves endpoints and rejects invalid final pixels and trailing data without per-pixel stream calls.");
    }
}
