using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;
using HeimKit.Shared;
using UnityEngine;

internal static class PerformanceRegressionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() { CandidateIndex(); SaveGenerations(); ReconciliationInputs(); ObserverTargets(); }
    private static void CandidateIndex()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var singleton = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        var world = (ZDOMan)RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan));
        var records = new Dictionary<ZDOID, ZDO>();
        typeof(ZDOMan).GetField("m_objectsByID", fields)!.SetValue(world, records);
        singleton.SetValue(null, world);
        ZDO Record(uint id, int prefab, float x = 0)
        {
            var record = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); record.m_uid = new ZDOID(41, id);
            typeof(ZDO).GetField("m_prefab", fields)!.SetValue(record, prefab);
            typeof(ZDO).GetField("m_position", fields)!.SetValue(record, new Vector3(x, 0, 0));
            records[record.m_uid] = record;
            return record;
        }
        int queries = 0;
        void Query(Vector2s center, SimulationDistance distance, List<ZDO> result, List<ZDO>? distant)
        {
            queries++;
            foreach (var record in records.Values)
            {
                var sector = record.GetSector();
                if (Math.Abs(sector.x - center.x) <= distance.NearSimulationDistance && Math.Abs(sector.y - center.y) <= distance.NearSimulationDistance) result.Add(record);
            }
        }
        try
        {
            var saved = Record(1, 7); Record(2, 9);
            using var index = new WorldCandidateIndex(z => z.GetPrefab() == 7, Query, observe: false);
            var output = new List<ZDO>();
            void Read(float x = 0) => index.Read(new Vector3(x, 0, 0), 1, output, classic: true);
            Read(); Check(queries == 1 && output.Count == 1, "Initial saved-object discovery failed.");
            for (int i = 0; i < 10; i++) Read();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Read();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(queries == 1 && allocated == 0, "Idle indexed passes queried or allocated: " + allocated);
            var created = Record(3, 7); index.Enqueue(created.m_uid); Read();
            Check(queries == 1 && output.Count == 2, "New replicated/local candidate did not wake the index.");
            int classifications = 0;
            using (var ownership = new WorldCandidateIndex(z => { classifications++; return z.GetPrefab() == 7; }, Query, observe: false))
            {
                ownership.Read(Vector3.zero, 1, output, classic: true);
                int observed = classifications;
                ownership.Enqueue(created.m_uid); // Ownership-only notification, with unchanged data/prefab.
                ownership.Read(Vector3.zero, 1, output, classic: true);
                Check(classifications > observed && output.Count == 2, "Ownership-only hint did not re-evaluate current eligibility.");
            }
            queries--; // Exclude the independent ownership consumer's seed from this scope's query count.
            typeof(ZDO).GetField("m_prefab", fields)!.SetValue(created, 9); index.Enqueue(created.m_uid); Read();
            Check(output.Count == 1, "Changed prefab/eligibility remained indexed.");
            typeof(ZDO).GetField("m_position", fields)!.SetValue(saved, new Vector3(640, 0, 0)); index.Enqueue(saved.m_uid); Read();
            Check(output.Count == 0, "Movement retained the old spatial cell.");
            Read(640); Check(output.Count == 1 && queries == 2, "Area entry missed a moved candidate.");
            // Destruction callbacks run before native release; consumption occurs later.
            world.m_onZDODestroyed?.Invoke(saved); records.Remove(saved.m_uid); Read(640);
            Check(output.Count == 0, "Confirmed destruction retained a candidate.");
            saved.m_uid = new ZDOID(41, 4); records[saved.m_uid] = saved; index.Enqueue(saved.m_uid); Read(640);
            Check(output.Count == 1 && output[0].m_uid.ID == 4, "Pooled object identity was confused with its old ID.");
            // Queue overflow must invalidate covered areas and rediscover saved objects.
            for (uint i = 100; i < 100 + WorldCandidateIndex.Capacity + 1; i++) index.Enqueue(new ZDOID(41, i));
            Read(640); Check(queries == 3 && output.Count == 1, "Queue overflow silently lost discovery coverage.");
            using (var reconnect = new WorldCandidateIndex(z => z.GetPrefab() == 7, Query, observe: false))
            { reconnect.Read(new Vector3(640, 0, 0), 1, output, classic: true); Check(queries == 4 && output.Count == 1, "Reconnect skipped initial discovery."); }
            for (uint i = 100; i < 100 + WorldCandidateIndex.Capacity + 1; i++) Record(i, 7);
            using (var crowded = new WorldCandidateIndex(z => z.GetPrefab() == 7, Query, observe: false))
            {
                crowded.Read(Vector3.zero, 1, output, classic: true);
                Check(output.Count > WorldCandidateIndex.Capacity, "Candidate overflow truncated fallback records.");
                long beforeRetry = crowded.Queries;
                crowded.Read(Vector3.zero, 1, output, classic: true);
                Check(crowded.Queries == beforeRetry + 1 && output.Count > WorldCandidateIndex.Capacity, "Incomplete discovery was marked complete.");
            }
            singleton.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan)));
            Read(640); Check(output.Count == 0, "World replacement reused old candidates.");
            System.Console.WriteLine("PASS: saved/new/changed/moved/destroyed/pooled records, area entry, overflow, reconnect and world replacement; 1000 warmed idle passes used zero queries and zero allocated bytes versus 1000 broad-discovery queries.");
        }
        finally { singleton.SetValue(null, previous); }
    }
    private static void SaveGenerations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-save-generations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var map = new SharedMapExploration(64, 12, 24, 2);
            var store = new SharedMapStore(directory, 123, map);
            map.Reveal(-120, 0);
            store.SaveIfDue(0, 60);
            var first = map.ExportPacked();
            map.Reveal(120, 0);
            // Lifecycle flush must join snapshot one and persist the later generation.
            Check(store.Save(), "Outstanding/newer map state was not flushed.");
            var restored = new SharedMapExploration(64, 12, 24, 2);
            Check(new SharedMapStore(directory, 123, restored).Load() && map.ExportPacked().SequenceEqual(restored.ExportPacked()), "A later generation was acknowledged without being saved.");
            var backup = Path.Combine(directory, "backup"); Directory.CreateDirectory(backup);
            File.Copy(store.FilePath + ".bak", Path.Combine(backup, Path.GetFileName(store.FilePath)));
            var older = new SharedMapExploration(64, 12, 24, 2);
            Check(new SharedMapStore(backup, 123, older).Load() && older.ExportPacked().SequenceEqual(first), "Worker snapshot read later mutable exploration.");
            Check(!store.Save() && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Clean state rewrote or temporary file leaked.");
            System.Console.WriteLine("PASS: background map snapshots preserve later changes, acknowledged generations, lifecycle flushing, native format and atomic backups.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void ReconciliationInputs()
    {
        var empty = Array.Empty<MapMarker>();
        MapMarker[] Marker(string label = "A", float x = 1, string id = "source") => new[] { new MapMarker(label, x, 2, 3, id) };
        SharedMapReconciliationState State(int pixels = 1, long pins = 1, long removed = 1, long publications = 1, float hide = 10,
            IReadOnlyList<MapMarker>? portals = null, IReadOnlyList<MapMarker>? resources = null, IReadOnlyList<MapMarker>? dungeons = null,
            IReadOnlyList<MapMarker>? runes = null, IReadOnlyList<MapMarker>? spawners = null) =>
            new(pixels, pins, removed, publications, hide, portals ?? empty, resources ?? empty, dungeons ?? empty, runes ?? empty, spawners ?? empty);
        var original = State(); Check(original.Same(State()), "Identical community inputs missed reuse.");
        Check(!original.Same(State(pixels: 2)) && !original.Same(State(pins: 2)) && !original.Same(State(removed: 2)) &&
            !original.Same(State(publications: 2)) && !original.Same(State(hide: 11)), "Exploration/pin/admin/publication inputs failed to invalidate.");
        Check(!original.Same(State(portals: Marker())) && !original.Same(State(resources: Marker())) && !original.Same(State(dungeons: Marker())) &&
            !original.Same(State(runes: Marker())) && !original.Same(State(spawners: Marker())), "A marker family failed to invalidate.");
        var portalState = State(portals: Marker());
        Check(portalState.Same(State(portals: Marker())) && !portalState.Same(State(portals: Marker("renamed"))) &&
            !portalState.Same(State(portals: Marker(x: 5))) && !portalState.Same(State(portals: Marker(id: "other"))), "Marker rename/offset/identity failed to invalidate.");
        var mutableList = Marker(); var captured = State(portals: mutableList); mutableList[0] = Marker("later")[0];
        Check(captured.Same(portalState), "Captured marker input was mutable.");
        System.Console.WriteLine("PASS: exact reconciliation inputs cover all marker families, exploration, adoption/checkmarks, administrative removals and publication offsets.");
    }
    private static void ObserverTargets()
    {
        var targets = WorldRecordChanges.NativeTargets().ToArray();
        foreach (string name in new[] { "Deserialize", "SetPrefab", "InternalSetPosition", "SetOwnerInternal", "SetRotation", "RemoveFloat", "RemoveInt", "RemoveLong", "RemoveByteArray" })
            Check(targets.Any(m => m.Name == name), "Missing native record observation path: " + name);
        foreach (Type type in new[] { typeof(float), typeof(int), typeof(long), typeof(bool), typeof(string), typeof(byte[]), typeof(Vector3), typeof(Quaternion) })
            Check(targets.Any(m => m.Name == "Set" && m.GetParameters()[0].ParameterType == typeof(int) && m.GetParameters()[1].ParameterType == type), "Missing typed local field observation: " + type.Name);
        const BindingFlags native = BindingFlags.Instance | BindingFlags.NonPublic;
        _ = typeof(WearNTear).GetMethod("ShouldUpdate", native)!.CreateDelegate(typeof(Func<WearNTear, float, bool>));
        _ = typeof(WearNTear).GetMethod("IsUnderWater", native)!.CreateDelegate(typeof(Func<WearNTear, bool>));
        _ = typeof(WearNTear).GetMethod("CanBeRemoved", native)!.CreateDelegate(typeof(Func<WearNTear, bool>));
        Check(typeof(Piece).GetField("m_creator", native)?.FieldType == typeof(long) &&
            typeof(Piece).GetField("m_creatorPlatformUserIDIndex", native)?.FieldType == typeof(int), "Cached creator attribution fields changed type.");
        System.Console.WriteLine("PASS: installed native observer targets include replicated data, ownership-only updates, movement, prefab assignment and typed local writes/removals.");
    }
}


