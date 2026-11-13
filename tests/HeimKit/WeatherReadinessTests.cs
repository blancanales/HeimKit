using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.World;

internal static class WeatherReadinessTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        StableAndChangedDependencies();
        SceneSelections();
        TrimVisitedCells();
        RecoverConcentratedFallback();
        FallbackReadiness();
    }
    private static void TrimVisitedCells()
    {
        using var world = new ReviewRegressionTests.World();
        const int recordsPerCell = 128, floor = 512;
        int pressureCells = WorldCandidateIndex.Capacity * 3 / 4 / recordsPerCell;
        var cells = new Dictionary<Vector2s, List<ZDO>>();
        var oldest = new Vector2s(-20, -20);
        int oldestQueries = 0;
        uint id = 1;
        for (int i = 0; i <= pressureCells; i++)
        {
            int x = i % 40 - 20, z = i / 40 - 20;
            var cell = new Vector2s(x, z);
            var records = new List<ZDO>(recordsPerCell);
            for (int j = 0; j < recordsPerCell; j++) records.Add(world.Add(id++, x, z));
            cells.Add(cell, records);
        }
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(),
            (sector, _, near, _) =>
            {
                if (sector == oldest) oldestQueries++;
                if (cells.TryGetValue(sector, out var records)) near.AddRange(records);
            }, observe: false);
        using var selection = provider.Subscribe("membership pressure", _ => true);
        var result = new List<ZDO>();
        var distance = new SimulationDistance(0, 0, true);
        provider.BeginCycle(true);
        ServerPerformance.CandidateTrims.Take(); ServerPerformance.CandidateFallbacks.Take();
        for (int i = 0; i <= pressureCells; i++)
        {
            var cell = new Vector2s(i % 40 - 20, i / 40 - 20);
            result.Clear();
            selection.Query(cell, distance, result);
            Check(selection.Indexed && result.Count == recordsPerCell, "Seeding under pressure lost targeted membership.");
        }
        var recent = new Vector2s(pressureCells % 40 - 20, pressureCells / 40 - 20);
        for (int i = 0; i < 8; i++)
        {
            result.Clear();
            selection.Query(recent, distance, result);
            Check(selection.Indexed && selection.Count >= floor * recordsPerCell, "Membership trimming crossed the cell floor or entered fallback.");
        }
        Check(selection.Count <= WorldCandidateIndex.Capacity / 2 &&
            ServerPerformance.CandidateTrims.Take().Calls == cells.Count - floor &&
            ServerPerformance.CandidateFallbacks.Take().Calls == 0, "Trimming did not settle at its target without fallback.");
        provider.BeginCycle(true);
        result.Clear();
        selection.Query(oldest, distance, result);
        Check(oldestQueries == 2 && result.Count == recordsPerCell && new HashSet<ZDO>(result).SetEquals(cells[oldest]), "An evicted least-recently-used cell was not seeded again.");
    }
    private static void RecoverConcentratedFallback()
    {
        using var world = new ReviewRegressionTests.World();
        var records = new List<ZDO>();
        for (uint id = 1; id <= WorldCandidateIndex.Capacity + 1; id++) records.Add(world.Add(id));
        int accepted = records.Count;
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(),
            (sector, _, near, _) =>
            {
                if (sector != default) return;
                for (int i = 0; i < accepted; i++) near.Add(records[i]);
            }, observe: false);
        using var selection = provider.Subscribe("concentrated membership", _ => true);
        var result = new List<ZDO>();
        void Query()
        {
            provider.BeginCycle(true);
            result.Clear();
            selection.Query(default, new SimulationDistance(0, 0, true), result);
        }
        ServerPerformance.CandidateTrims.Take(); ServerPerformance.CandidateFallbacks.Take();
        Query();
        Check(selection.Fallback && result.Count == accepted && ServerPerformance.CandidateTrims.Take().Calls == 0,
            "Concentrated records did not fall back at the record cap.");
        accepted = 1;
        provider.MaintainSelections(10); Query();
        Check(selection.Indexed && result.Count == 1 && result[0] == records[0], "First fallback did not recover with smaller live coverage.");
        accepted = records.Count;
        for (int i = 1; i < accepted; i++) provider.Enqueue(records[i].m_uid);
        Query();
        Check(selection.Fallback && ServerPerformance.CandidateFallbacks.Take().Calls == 2, "Expected one event for each fallback transition.");
        accepted = 1;
        provider.MaintainSelections(129.99f); Query();
        Check(selection.Fallback && result.Count == 1, "Fallback retried before 120 seconds elapsed.");
        provider.MaintainSelections(130); Query();
        Check(selection.Indexed && result.Count == 1, "Fallback did not retry at the 120-second boundary.");
    }
    private static void FallbackReadiness()
    {
        using var world = new ReviewRegressionTests.World();
        for (uint id = 1; id <= WorldCandidateIndex.Capacity + 1; id++) world.Add(id);
        int nativeQueries = 0;
        long binding = 1;
        float now = 10;
        bool loaded = true;
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(),
            (sector, _, near, _) =>
            {
                nativeQueries++;
                if (sector == default) near.AddRange(world.Records.Values);
            }, (_, _, _, _) => true, observe: false);
        provider.BeginCycle(true);
        using var readiness = new SectorReadiness(provider, "fallback readiness", _ => true, _ => loaded, () => binding, () => now);
        ServerPerformance.CandidateFallbacks.Take();
        Check(readiness.Ready(default) && ServerPerformance.CandidateFallbacks.Take().Calls == 1, "Readiness test did not enter fallback.");
        int initialQueries = nativeQueries;
        now = 10.99f; loaded = false;
        provider.BeginCycle(true);
        Check(readiness.Ready(default) && readiness.Reuses == 1 && nativeQueries == initialQueries, "Fallback readiness was not reused within one second.");
        now = 11;
        Check(!readiness.Ready(default) && readiness.Checks == 2 && nativeQueries > initialQueries, "Fallback readiness did not expire at one second.");
        binding++; loaded = true;
        Check(readiness.Ready(default) && readiness.Checks == 3, "A binding change failed to invalidate fallback readiness immediately.");
        binding = -1; loaded = false;
        Check(!readiness.Ready(default) && !readiness.Ready(default) && readiness.Checks == 5 && readiness.Reuses == 1,
            "Fallback readiness cached without native binding observation.");
    }
    private static void StableAndChangedDependencies()
    {
        using var world = new ReviewRegressionTests.World();
        var loaded = new HashSet<ZDOID>();
        for (uint id = 1; id <= 1000; id++) loaded.Add(world.Add(id).m_uid);
        int nativeQueries = 0, bindingChecks = 0; long bindings = 1;
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(),
            (sector, _, near, _) =>
            {
                nativeQueries++;
                foreach (var record in world.Records.Values) if (record.GetSector() == sector) near.Add(record);
            }, (_, _, _, _) => true, observe: false);
        provider.BeginCycle(true);
        using var readiness = new SectorReadiness(provider, "readiness test", r => r.GetPrefab() == 7,
            r => { bindingChecks++; return loaded.Contains(r.m_uid); }, () => bindings);
        var cell = new Vector2s(0, 0);
        Check(readiness.Ready(cell), "A complete dependency neighborhood was not ready.");
        int initialQueries = nativeQueries, initialChecks = bindingChecks;
        Check(initialChecks == 1000, "The initial check omitted a required dependency.");
        for (int frame = 0; frame < 100; frame++)
        {
            provider.BeginCycle(true);
            // Accepted ordinary state updates retain the same membership.
            provider.Enqueue(world.Records.Keys.First());
            Check(readiness.Ready(cell), "A stable dependency neighborhood lost readiness across frames.");
        }
        Check(bindingChecks == initialChecks && nativeQueries == initialQueries && readiness.Reuses == 100,
            "Stable frames or ordinary state writes repeated full neighborhood queries/binding checks.");
        var addition = world.Add(1001); provider.Enqueue(addition.m_uid);
        Check(!readiness.Ready(cell), "A newly accepted but unloaded dependency reused stale positive readiness.");
        loaded.Add(addition.m_uid); bindings++;
        Check(readiness.Ready(cell), "A new scene binding did not recover readiness.");
        loaded.Remove(addition.m_uid); bindings++;
        Check(!readiness.Ready(cell), "A lost scene binding did not invalidate readiness.");
        world.Move(addition, 5, 0); provider.Enqueue(addition.m_uid);
        Check(readiness.Ready(cell), "A dependency moving outside the neighborhood retained stale negative readiness.");
        var removed = world.Records.Values.First(); loaded.Remove(removed.m_uid); bindings++;
        Check(!readiness.Ready(cell), "A missing required binding was not observed.");
        var removedId = removed.m_uid; world.Remove(removed); provider.Enqueue(removedId);
        Check(readiness.Ready(cell), "Accepted terminal removal did not retire a missing dependency.");
        var switched = world.Records.Values.First(r => r.GetSector() == cell);
        loaded.Remove(switched.m_uid); bindings++;
        Check(!readiness.Ready(cell), "The replacement scenario was not initially blocked.");
        NativeAccess.Field<ZDO, int>("m_prefab")(switched) = 8; provider.Enqueue(switched.m_uid);
        Check(readiness.Ready(cell), "A dependency becoming irrelevant did not invalidate negative readiness.");
        long before = readiness.Checks; readiness.Reset(); Check(readiness.Ready(cell) && readiness.Checks == before + 1,
            "An explicit scope/mask reset retained old readiness results.");
        bindings = -1; before = readiness.Checks; readiness.Ready(cell); readiness.Ready(cell);
        Check(readiness.Checks == before + 2, "Absent native binding observation allowed cross-frame caching.");
        System.Console.WriteLine("PASS: 100 stable frames over 1000 dependencies reuse readiness without repeat discovery/binding walks; add/move/remove/prefab and scene transitions invalidate it, and absent observation disables caching.");
    }
    private static void SceneSelections()
    {
        using var world = new ReviewRegressionTests.World();
        var built = world.Add(2001); var loose = world.Add(2002); var remote = world.Add(2003, 20, 0);
        NativeAccess.Field<ZDO, int>("m_prefab")(loose) = 8;
        var owner = NativeAccess.Method<Action<ZDO, long>>(typeof(ZDO), "SetOwnerInternal", typeof(long));
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(),
            (sector, _, near, _) => { foreach (var r in world.Records.Values) if (r.GetSector() == sector) near.Add(r); },
            (_, _, _, _) => true, observe: false);
        using var selection = provider.Subscribe("scene test", r => r.GetPrefab() == 7 || r.GetOwner() == 99);
        var result = new List<ZDO>(); provider.BeginCycle(true);
        selection.Query(new Vector2s(0, 0), new SimulationDistance(1, 0), result);
        Check(result.SequenceEqual(new[] { built }), "Scene selection included an unrelated remote/client record or lost a structural dependency.");
        owner(loose, 99); provider.Enqueue(loose.m_uid); result.Clear();
        selection.Query(new Vector2s(0, 0), new SimulationDistance(1, 0), result);
        Check(result.Contains(built) && result.Contains(loose), "An accepted server ownership transition did not retain a transient record.");
        owner(loose, 0); provider.Enqueue(loose.m_uid); result.Clear();
        selection.Query(new Vector2s(0, 0), new SimulationDistance(1, 0), result);
        Check(result.Count == 1 && result[0] == built, "A transient record remained selected after native ownership release.");
        result.Clear(); selection.Query(new Vector2s(20, 0), new SimulationDistance(1, 0), result);
        Check(result.Count == 1 && result[0] == remote, "A disjoint player region lost its independent scene dependencies.");
        System.Console.WriteLine("PASS: indexed scene selection preserves structural/disjoint-region dependencies and responds to native ownership transitions without reclassifying every retained object per scene pass.");
    }
}
