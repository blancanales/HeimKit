using System.Reflection;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using HeimKit.Core.World;
using UnityEngine;

internal static class LoggingCoreFixTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { TerrainCompatibility(); StableDrain(); RawMembership(); }
    private static void TerrainCompatibility()
    {
        var holder = typeof(TerrainContext).GetNestedType("Accessors", BindingFlags.NonPublic)!;
        Check((holder.Attributes & TypeAttributes.BeforeFieldInit) == 0, "Terrain native access can initialize before its compatibility check.");
        TerrainContext.CheckCompatibility();
        var missing = new MissingFieldException("renamed field");
        bool wrapped = false;
        try { TerrainContext.ValidateNative(() => throw missing); }
        catch (MissingMemberException error) { wrapped = error.GetType() == typeof(MissingMemberException) && error.Message == "Native terrain context APIs." && ReferenceEquals(error.InnerException, missing); }
        Check(wrapped, "Terrain incompatibility lost its actionable error or original cause.");
        System.Console.WriteLine("PASS: terrain native APIs resolve through a non-beforefieldinit holder and missing members retain the specified compatibility error and cause.");
    }
    private static void StableDrain()
    {
        var samples = new ActivitySamples();
        var pending = (Dictionary<long, PlayerActivityView>)typeof(ActivitySamples).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(samples)!;
        var views = Enumerable.Range(0, 24).Select(i => new PlayerActivityView { Peer = i + 1, Known = i % 3 != 0, Idle = i % 4 < 2 }).ToArray();
        var random = new System.Random(17);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            for (int i = views.Length - 1; i > 0; i--) { int other = random.Next(i + 1); (views[i], views[other]) = (views[other], views[i]); }
            pending.Clear(); foreach (var view in views) pending.Add(view.Peer, view);
            int count = iteration % 25;
            var expected = pending.Values.OrderBy(view => view.Known).ThenByDescending(view => view.Idle).Take(count).Select(view => view.Peer).ToArray();
            Check(samples.Drain(count).Select(view => view.Peer).SequenceEqual(expected), "Tied activity samples changed stable enumeration order.");
        }
        for (int i = 0; i < 30; i++) { foreach (var view in views) pending[view.Peer] = view; samples.Drain(6); }
        var borrowed = samples.Drain(0); long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { foreach (var view in views) pending[view.Peer] = view; samples.Drain(6); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before && ReferenceEquals(borrowed, samples.Drain(0)), "Warmed stable Drain allocated or stopped reusing its borrowed list.");
        System.Console.WriteLine("PASS: activity Drain matches stable LINQ ordering over 200 tied permutations and reuses its result with zero warmed allocations.");
    }
    private static void RawMembership()
    {
        using var world = new ReviewRegressionTests.World();
        var first = world.Add(1); var moved = world.Add(2); var destroyed = world.Add(3); world.Add(4, 1); world.Add(5, 1);
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(), observe: false);
        provider.BeginCycle(true); using var target = provider.Subscribe("target", _ => true);
        var actual = new List<ZDO>(); var expected = new List<ZDO>();
        void CheckNative()
        {
            actual.Clear(); expected.Clear();
            provider.For("broad").Query(default, new SimulationDistance(1, 0, true), actual);
            world.Manager.FindSectorObjects(default, new SimulationDistance(1, 0, true), expected);
            Check(actual.SequenceEqual(expected), "Cached broad order differs from native after an accepted change.");
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var cells = (Dictionary<Vector2s, List<ZDO>>)typeof(WorldObjectProvider).GetField("_cycle", fields)!.GetValue(provider)!;
            var locations = (Dictionary<ZDOID, Vector2s>)typeof(WorldObjectProvider).GetField("_rawLocations", fields)!.GetValue(provider)!;
            var objects = (Dictionary<ZDOID, ZDO>)typeof(WorldObjectProvider).GetField("_rawObjects", fields)!.GetValue(provider)!;
            int records = 0;
            foreach (var cell in cells) foreach (var record in cell.Value)
            {
                records++; Check(locations.TryGetValue(record.m_uid, out var where) && where == cell.Key && objects.TryGetValue(record.m_uid, out var cached) && ReferenceEquals(cached, record), "Raw identity maps diverged from cached cells.");
            }
            Check(records == locations.Count && objects.Count == locations.Count, "Raw maps retained stale or duplicate identities.");
        }
        CheckNative(); provider.Enqueue(moved.m_uid); CheckNative();
        world.Move(moved, 1, 0); provider.Enqueue(moved.m_uid); CheckNative();
        var oldDestroyed = destroyed.m_uid; world.Remove(destroyed); provider.Enqueue(oldDestroyed); CheckNative();
        var created = world.Add(6); provider.Enqueue(created.m_uid); CheckNative();
        var oldFirst = first.m_uid; world.Remove(first); world.Reuse(first, 7, -1, 0);
        provider.Enqueue(first.m_uid); provider.Enqueue(oldFirst); CheckNative();
        var extra = world.Add(9); provider.Enqueue(extra.m_uid); CheckNative();
        var oldCreated = created.m_uid; world.Remove(created); world.Reuse(created, 8, 0, 0);
        provider.Enqueue(created.m_uid); provider.Enqueue(oldCreated); CheckNative();
        System.Console.WriteLine("PASS: same-cell changes, moves, destruction, creation and pooled reuse preserve exact native broad order and synchronized O(1) raw identity membership.");
    }
}
