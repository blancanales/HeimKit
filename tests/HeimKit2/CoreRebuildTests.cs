using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using HeimKit.Core.Native;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using System.Globalization;
using UnityEngine;
using BepInEx.Logging;
using HeimKit.Core.Players.Models;

internal static class CoreRebuildTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run() { SharedDiscovery(); ReviewRegressionTests.Run(); LoggingCoreFixTests.Run(); NativeOrdering(); PrefabHeadroom(); Schedule(); NativeFields(); ActivityBuffers(); ModuleIsolation(); DisplayLabels(); HotPathClosures(); }
    private static void NativeOrdering()
    {
        var original = CultureInfo.CurrentCulture;
        var ids = new[] { new ZDOID(-12, 7), new ZDOID(1, 7), new ZDOID(12, 1), new ZDOID(-1, 9), new ZDOID(1, 70) };
        try
        {
            foreach (var locale in new[] { "en-US", "sv-SE", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);
                foreach (var a in ids) foreach (var b in ids)
                {
                    Check(Math.Sign(NativeIdentity.Compare(a, b)) == Math.Sign(string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCulture)), "Tame ordering changed with negative IDs or punctuation.");
                    Check(Math.Sign(NativeIdentity.CompareOrdinal(a, b)) == Math.Sign(string.CompareOrdinal(a.ToString(), b.ToString())), "Portal ordering changed.");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
        System.Console.WriteLine("PASS: tame and portal identity ordering match the original comparisons across three server locales, including negative IDs.");
    }
    private static void PrefabHeadroom()
    {
        var registry = new PrefabRegistry();
        for (int i = 0; i < 5000; i++) registry.Entry(i).Metadata[("test", typeof(int))] = i;
        for (int i = 0; i < 5000; i++) Check((int)registry.Entry(i).Metadata[("test", typeof(int))]! == i, "Shared prefab capacity lost metadata above the former limit.");
        registry.Clear();
        System.Console.WriteLine("PASS: shared prefab metadata retains more than 4096 definitions without disabling consumers.");
    }
    private static void HotPathClosures()
    {
        var codes = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode code) codes[code.Value] = code;
        var assembly = typeof(HeimKit.Plugin).Assembly;
        foreach (var target in new[] {
            ("InfiniteTorches.InfiniteTorchesController", "Tick"), ("WeatherGuard.WeatherGuardController", "Tick"),
            ("AFKNightWatch.AFKNightWatchController", "Tick"), ("AFKRaidWatch.AFKRaidWatchController", "Tick"),
            ("DoorKeeper.DoorKeeperController", "Tick"), ("ForestRenewal.ForestRenewalController", "Tick"),
            ("SeaSalvage.SeaSalvageController", "Tick"), ("HeimMender.HeimMenderController", "Tick"),
            ("SharedMap.SharedMapController", "Tick"), ("TameTravel.TameTravelController", "Tick"),
            ("TameTravel.TameTravelPlacement", "FindPosition") })
        {
            var method = assembly.GetType("HeimKit.Features." + target.Item1)!.GetMethod(target.Item2, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
            for (int index = 0; index < bytes.Length;)
            {
                short opcode = bytes[index++]; if (opcode == 0xfe) opcode = (short)(0xfe00 | bytes[index++]);
                var code = codes[opcode];
                if (code == OpCodes.Newobj)
                {
                    var type = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index))!.DeclaringType!;
                    Check(!type.Name.Contains("DisplayClass") || type.IsValueType, target.Item1 + "." + target.Item2 + " allocates a hidden closure before diagnostic gating.");
                }
                index += code.OperandType switch {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, index) * 4,
                    _ => 4 };
            }
        }
        System.Console.WriteLine("PASS: compiled controller and placement hot paths create no hidden closure objects before diagnostic gating.");
    }
    private static void DisplayLabels()
    {
        Check(HeimKit.Features.SharedMap.SharedMapDungeonPrefab.Labels["MorkBorg"] == "M\u00f6rkhalla", "The native dungeon label lost its original Unicode text.");
        System.Console.WriteLine("PASS: the port preserves the original Unicode dungeon label.");
    }
    private sealed class Presence : IPlayerPresence
    {
        public int PeerReads;
        public bool? HasConnectedPlayers => true;
        public bool? HasJoinedPlayers => true;
        public int? ConnectedCount => 1;
        public int? ReadyCount => 1;
        public int? JoinedCount => 1;
        public IReadOnlyList<ZNetPeer> Peers { get { PeerReads++; return Array.Empty<ZNetPeer>(); } }
        public void Refresh() { }
        public void Clear() { }
    }
    private static void SharedDiscovery()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var singleton = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        var world = (ZDOMan)RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan));
        var records = new Dictionary<ZDOID, ZDO>();
        typeof(ZDOMan).GetField("m_objectsByID", fields)!.SetValue(world, records); singleton.SetValue(null, world);
        ZDO Record(uint id, float x, bool distant = false)
        {
            var record = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); record.m_uid = new ZDOID(88, id);
            typeof(ZDO).GetField("m_prefab", fields)!.SetValue(record, 7);
            typeof(ZDO).GetField("m_position", fields)!.SetValue(record, new Vector3(x, 0, 0));
            if (distant) record.Distant = true;
            records.Add(record.m_uid, record); return record;
        }
        var queries = new Dictionary<Vector2s, int>();
        void Query(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? far)
        {
            Check(distance.NearSimulationDistance == 0, "Discovery did not use a single native cell.");
            queries.TryGetValue(sector, out int count); queries[sector] = count + 1;
            foreach (var record in records.Values) if (record.GetSector() == sector) near.Add(record);
        }
        bool Geometry(Vector2s a, Vector2s c, int radius, bool far)
        { float dx = a.x - c.x, dz = a.y - c.y, extent = radius + (far ? .8f : .5f); return dx * dx + dz * dz < extent * extent; }
        try
        {
            var a = Record(1, 0); var b = Record(2, 64, true); Record(3, 128);
            var presence = new Presence();
            using var provider = new WorldObjectProvider(presence, Query, Geometry, observe: false);
            provider.BeginCycle(true);
            int classifications = 0;
            using var first = provider.Subscribe("first", z => { classifications++; return z.GetPrefab() == 7; });
            using var second = provider.Subscribe("second", z => { classifications++; return z.GetPrefab() == 7; });
            var near = new List<ZDO>(); var far = new List<ZDO>();
            first.Query(new Vector2s(0, 0), new SimulationDistance(0, 1, true), near, far);
            Check(near.Count == 1 && near[0] == a && far.Count == 1 && far[0] == b, "Native near/distant split was changed.");
            near.Clear(); far.Clear(); second.Query(new Vector2s(0, 0), new SimulationDistance(1, 0, true), near, far);
            Check(near.Count == 2 && far.Count == 0 && queries.Values.All(n => n == 1), "Subscribers repeated a native cell or lost a near record.");
            var added = Record(4, 0); provider.Enqueue(added.m_uid);
            near.Clear(); second.Query(new Vector2s(0, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Contains(added) && queries[new Vector2s(0, 0)] == 1, "Accepted changes were hidden behind a cached snapshot.");
            for (int i = 0; i < 20; i++) { near.Clear(); first.Query(new Vector2s(0, 0), new SimulationDistance(1, 0, true), near); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { near.Clear(); first.Query(new Vector2s(0, 0), new SimulationDistance(1, 0, true), near); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed shared subscription allocated per query.");
            provider.BeginCycle(true); near.Clear(); second.Query(new Vector2s(0, 0), new SimulationDistance(1, 0, true), near);
            Check(queries.Values.All(n => n == 1), "Unchanged seeded cells required another native scan.");
            int classified = classifications;
            for (int i = 10; i < 1110; i++) { near.Clear(); first.Query(new Vector2s(i, 0), new SimulationDistance(0, 0, true), near); }
            near.Clear(); first.Query(new Vector2s(0, 0), new SimulationDistance(0, 0, true), near);
            Check(queries[new Vector2s(0, 0)] == 1 && classifications == classified, "Crossing 1024 cells globally reset discovery or repeated eligibility checks.");
            for (int i = 1110; i < 8310; i++) { near.Clear(); first.Query(new Vector2s(i, 0), new SimulationDistance(0, 0, true), near); }
            near.Clear(); first.Query(new Vector2s(0, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Count == 2 && queries[new Vector2s(0, 0)] == 1, "A recently read cell was evicted before older coverage.");
            near.Clear(); first.Query(new Vector2s(10, 0), new SimulationDistance(0, 0, true), near);
            Check(queries[new Vector2s(10, 0)] == 2, "Least-recently-used coverage was not rediscovered after eviction.");
            near.Clear(); first.Query(new Vector2s(8309, 0), new SimulationDistance(0, 0, true), near);
            Check(queries[new Vector2s(8309, 0)] == 1, "Cell pressure wiped unrelated recent discovery.");
            Check(presence.PeerReads == 0, "Actor positions were eagerly sampled during world discovery.");
            using (var scope = provider.CreateScope("lazy"))
            {
                scope.SampleActors(); int samples = presence.PeerReads;
                scope.SampleActors(); Check(samples > 0 && presence.PeerReads == samples, "Consumer views repeated actor sampling in a frame.");
            }
            // Populate an unseeded busy cell with more unrelated records than
            // the old all-object index's entire identity budget.
            for (uint i = 1000; i < 1000 + WorldCandidateIndex.Capacity + 1; i++)
            {
                var unrelated = Record(i, 64 * 16000);
                typeof(ZDO).GetField("m_prefab", fields)!.SetValue(unrelated, 9);
            }
            var target = Record(999, 64 * 16000);
            near.Clear(); first.Query(new Vector2s(16000, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Count == 1 && near[0] == target, "Busy cells lost targeted candidates.");
            classified = classifications;
            provider.BeginCycle(true); near.Clear(); first.Query(new Vector2s(16000, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Count == 1 && queries[new Vector2s(16000, 0)] == 1 && classifications == classified, "Unrelated objects caused capacity rebuilds or per-pass classification.");
            near.Clear(); provider.For("broad_a").Query(new Vector2s(16000, 0), new SimulationDistance(0, 0, true), near);
            int broadCount = near.Count;
            near.Clear(); provider.For("broad_b").Query(new Vector2s(16000, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Count == broadCount && broadCount == WorldCandidateIndex.Capacity + 2 && queries[new Vector2s(16000, 0)] == 2, "Broad consumers did not share the frame result.");
            var extra = Record(998, 64 * 16000); provider.Enqueue(extra.m_uid);
            near.Clear(); provider.For("broad_b").Query(new Vector2s(16000, 0), new SimulationDistance(0, 0, true), near);
            Check(near.Count == broadCount + 1 && queries[new Vector2s(16000, 0)] == 2, "A broad cached view missed a same-frame accepted change.");
            using (var failed = provider.Subscribe("failed_predicate", _ => throw new InvalidOperationException("predicate failure")))
            {
                near.Clear(); first.Query(new Vector2s(0, 0), new SimulationDistance(0, 0, true), near);
                Check(near.Count == 2, "Another subscriber's classifier failure stopped healthy discovery.");
                bool rejected = false;
                try { failed.Query(new Vector2s(0, 0), new SimulationDistance(0, 0, true), near); }
                catch (InvalidOperationException error) { rejected = error.InnerException?.Message == "predicate failure"; }
                Check(rejected, "Classification failure was not returned to its owning consumer.");
            }
            provider.BeginCycle(false); near.Clear(); second.Query(new Vector2s(0, 0), new SimulationDistance(1, 0, true), near);
            Check(near.Count == 0, "Unavailable world leaked old candidate views.");
            var abandoned = provider.CreateScope("failed_constructor"); abandoned.Index(_ => true);
            provider.ReleaseOwner("failed_constructor");
            Check(((System.Collections.ICollection)typeof(WorldObjectProvider).GetField("_scopes", fields)!.GetValue(provider)!).Count == 0,
                "Failed construction left a Core scope registered.");
        }
        finally { singleton.SetValue(null, previous); }
        System.Console.WriteLine("PASS: targeted discovery preserves geometry and changes, evicts locally across 8300 cells, survives 131073 unrelated objects without rebuilding, isolates classifiers, shares broad views, samples lazily and allocates zero bytes over 1000 warmed queries.");
    }
    private static void Schedule()
    {
        var scheduler = new ModuleScheduler(); var a = new object(); var b = new object();
        Check(scheduler.Due(a, "work", 0, 2) && !scheduler.Due(b, "work", 0, 2), "Startup work was not staggered.");
        Check(!scheduler.Due(a, "work", 1, 2) && scheduler.Due(b, "work", .03f, 2), "Configured intervals or startup phase changed.");
        Check(scheduler.Due(a, "work", 2, 2) && !scheduler.Due(a, "work", 2, 2), "Scheduler repeated due work.");
        scheduler.Forget(a); Check(scheduler.Due(a, "work", 10, 2) == false, "A new registration lost its stagger.");
        System.Console.WriteLine("PASS: Core scheduling preserves configured intervals, separates startup phases, suppresses duplicate passes and removes disposed registrations.");
    }
    private sealed class Fields { private bool _flag = false; private long _creator = 0; public bool Flag => _flag; public long Creator => _creator; }
    private static void NativeFields()
    {
        var flag = NativeAccess.Field<Fields, bool>("_flag"); var creator = NativeAccess.Field<Fields, long>("_creator"); var value = new Fields();
        flag(value) = true; creator(value) = 123;
        Check(value.Flag && value.Creator == 123 && ReferenceEquals(flag, NativeAccess.Field<Fields, bool>("_flag")), "Native access was not cached or did not mutate by reference.");
        for (int i = 0; i < 100; i++) flag(value) = !flag(value);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) flag(value) = !flag(value);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Typed native field access boxed values.");
        bool failed = false; try { NativeAccess.Field<Fields, int>("_flag"); } catch (MissingFieldException) { failed = true; }
        Check(failed, "Incompatible native fields were accepted.");
        System.Console.WriteLine("PASS: cached typed native FieldRefs preserve exact types and assignment, reject incompatible members and allocate zero bytes over 1000 field updates.");
    }
    private static void ActivityBuffers()
    {
        var options = new PlayerActivityOptions(10, 0, .5f, 10, 1);
        var tracker = new PlayerActivityTracker(options);
        var actor = new PlayerActivityObservation { Peer = 99, Character = "same_actor", Known = true, Alive = true, Health = 100, Equipment = "none" };
        var input = new[] { actor };
        tracker.Update(0, input); actor.Equipment = "sword";
        var active = tracker.Update(1, input)[0];
        Check(active.LastSignal == "equipment_changed", "A reused input object overwrote the prior equipment observation.");
        for (int i = 2; i < 30; i++) tracker.Update(i, input);
        Check(active.Idle, "Buffered observations changed idle timing.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 30; i < 1030; i++) tracker.Update(i, input);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed activity tracking allocated per sample.");
        tracker.Signal(99, 1030); actor.X = 1;
        Check(tracker.Update(1031, input)[0].LastSignal == "movement", "A routed signal aliased the movement anchor to mutable input.");
        System.Console.WriteLine("PASS: reused actor inputs preserve prior equipment, movement anchors, routed activity and idle timing; 1000 warmed tracker samples allocate zero bytes.");
    }
    private sealed class Module : IServerModule
    {
        public string Name { get; set; } = "Module";
        public bool IsActive { get; private set; }
        public bool Fail;
        public int Ticks, Stops, Reports;
        public void Initialize(string version) => IsActive = true;
        public void Tick(float now) { Ticks++; if (!IsActive) { Reports++; return; } if (Fail) throw new InvalidOperationException("isolated failure"); }
        public void Shutdown() { Stops++; IsActive = false; }
    }
    private static void ModuleIsolation()
    {
        using var log = new ManualLogSource("Module host checks");
        var failed = new Module { Name = "Failed", Fail = true }; var healthy = new Module { Name = "Healthy" };
        using (var host = new ModuleHost(log))
        {
            host.Register(() => failed, failed.Name); host.Register(() => healthy, healthy.Name);
            host.Initialize("test"); host.Tick(0); host.Tick(1);
            Check(!failed.IsActive && failed.Ticks == 2 && failed.Reports == 1 && failed.Stops == 1 && healthy.Ticks == 2 && healthy.IsActive, "A failed module lost administration, repeated gameplay or stopped an independent module.");
        }
        Check(healthy.Stops == 1, "Host disposal skipped an independent module.");
        System.Console.WriteLine("PASS: Core host disables a failed module once, continues other modules and attempts every shutdown.");
    }
}
