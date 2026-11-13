using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.SharedMap;
using UnityEngine;

internal static class ArrivalPerformanceTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private class Module : IServerModule
    {
        public string Name { get; }
        public bool IsActive { get; set; } = true;
        internal int Calls;
        internal Action? Work;
        internal Module(string name) => Name = name;
        public void Initialize(string version) { }
        public void Tick(float now) { Calls++; Work?.Invoke(); }
        public void Shutdown() { }
    }
    private sealed class Immediate : Module, IImmediateActivation { internal Immediate() : base("ArrivalProtectionTest") { } }
    internal static void Run() { Startup(); IncrementalIds(); EarlyCaptureAndPinAdmission(); }
    private static void Startup()
    {
        DiagnosticLog.StartSession();
        long clock = 1; var scheduler = new ModuleScheduler(() => clock);
        using var log = new ManualLogSource("Arrival startup");
        using (var host = new ModuleHost(log, scheduler))
        {
            var a = new Module("ArrivalConvenienceA"); var b = new Module("ArrivalConvenienceB"); var protection = new Immediate(); var admin = new Module("ArrivalAdmin") { IsActive = false };
            protection.Work = () => clock += Stopwatch.Frequency / 1000;
            host.Register(() => a, a.Name); host.Register(() => b, b.Name); host.Register(() => protection, protection.Name); host.Register(() => admin, admin.Name);
            host.Tick(0, true);
            Check(protection.Calls == 1 && a.Calls == 0 && b.Calls == 0 && admin.Calls == 1, "Initial protection did not reserve its frame, or inactive administration was suppressed.");
            host.Tick(.04f, true);
            Check(a.Calls == 1 && b.Calls == 0 && protection.Calls == 2, "More than one optional startup ran in a frame.");
            host.Tick(.08f, true);
            Check(a.Calls == 2 && b.Calls == 1, "Cold admission suppressed an already-running module or starved the next startup.");
            scheduler.Combat.SetRaid(true); host.Tick(.12f, true);
            Check(a.Calls == 3 && b.Calls == 2 && protection.Calls == 4, "Arrival startup rules paused ongoing module processing during combat.");
            scheduler.Combat.Clear(); host.Tick(.16f, false); host.Tick(.20f, true);
            Check(protection.Calls == 6 && a.Calls == 4 && b.Calls == 3, "Empty-to-occupied startup was not reset independently of ordinary empty ticks.");
        }
        var budget = new ModuleScheduler(() => clock);
        using (var host = new ModuleHost(log, budget))
        {
            var cold = new Module("ArrivalColdBudget"); host.Register(() => cold, cold.Name);
            budget.BeginFrame(1); clock += Stopwatch.Frequency / 100; host.Tick(1, true, beginFrame: false);
            Check(cold.Calls == 0, "ModuleHost reset the budget after shared player/world work consumed it.");
            budget.Combat.SetRaid(true);
            budget.BeginFrame(1.3f); clock += Stopwatch.Frequency / 100; host.Tick(1.3f, true, beginFrame: false);
            Check(cold.Calls == 0, "Startup starvation recovery bypassed battle admission.");
            budget.Combat.Clear();
            budget.BeginFrame(1.6f); clock += Stopwatch.Frequency / 100; host.Tick(1.6f, true, beginFrame: false);
            Check(cold.Calls == 1, "Persistent required work starved optional startup beyond its bounded progress allowance.");
        }
        System.Console.WriteLine("PASS: protection runs before optional startup, one cold module starts per update, warm/manual paths continue, rejoin resets admission, shared work consumes the same budget and bounded startup progress remains possible.");
    }
    private static void IncrementalIds()
    {
        using var world = new ReviewRegressionTests.World();
        var expected = new HashSet<ZDOID>();
        uint id = 10000;
        for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) expected.Add(world.Add(id++, x, z).m_uid);
        int queries = 0;
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(), (sector, _, near, _) =>
        { queries++; foreach (var r in world.Records.Values) if (r.GetSector() == sector) near.Add(r); }, (_, _, _, _) => true, observe: false);
        provider.BeginCycle(true);
        using var selection = provider.Subscribe("ArrivalScan", _ => true);
        var ids = new List<ZDOID>();
        using (var work = provider.CollectIds(selection, Vector3.zero, 2, true, ids).GetEnumerator())
        {
            Check(!WorkSlice.Advance(work, 1000, 3) && queries == 3 && ids.Count == 3, "Incremental discovery seeded a complete area before yielding.");
            provider.BeginCycle(true);
            while (!WorkSlice.Advance(work, 1000, 3)) provider.BeginCycle(true);
        }
        Check(ids.Count == expected.Count && ids.ToHashSet().SetEquals(expected), "Incremental discovery lost or duplicated stable source identities across frame-cache resets.");
        Check(provider.WithinNear(Vector3.zero, 2, true, new Vector3(64, 0, 0)) && !provider.WithinNear(Vector3.zero, 2, true, new Vector3(640, 0, 0)), "Final current-player scope validation admitted old distant discovery.");
        ids.Clear(); provider.BeginCycle(true);
        using (var cancelled = provider.CollectIds(selection, Vector3.zero, 2, true, ids).GetEnumerator())
        { cancelled.MoveNext(); }
        Check(world.Records.Count == 25 && ids.Count == 1, "Cancelled discovery modified world records or continued scanning.");
        provider.BeginCycle(true); ids.Clear();
        using (var stopped = provider.CollectIds(selection, Vector3.zero, 2, true, ids).GetEnumerator())
        {
            stopped.MoveNext(); provider.BeginCycle(false);
            Check(!stopped.MoveNext() && ids.Count == 1, "Discovery continued after world readiness was lost.");
        }
        System.Console.WriteLine("PASS: optional discovery yields at cell boundaries, retains only source IDs across frame resets, preserves scope and source records, and stops safely on cancellation/readiness loss.");
    }
    private static void EarlyCaptureAndPinAdmission()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Early arrival capture"); var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false);
        var policy = new DebugLoggingPolicy(config.Bind("PlayerActivity", "DebugMode", true), false);
        using var observer = new PlayerArrivalDiagnostics(new ReviewRegressionTests.Presence(), policy, log, new PlayerWeatherContext(), _ => ("Clear", false, true));
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 88; peer.m_playerName = "Early arrival";
        var actor = new ZDOID(88, 1);
        Check(observer.ObserveCharacter(peer, actor, 0), "Character announcement did not open pre-activation capture.");
        PerformanceStages.Record("HeimKit", "feature_dispatch", 480);
        observer.Capture(.1f, 502, 33, 0, 9);
        Check(observer.DeferPinComparison(Vector3.zero, .2f), "Unknown destination permitted expensive startup pin comparisons.");
        observer.ObservePosition(peer, actor, new Vector3(1000, 20, 0), .3f);
        Check(!observer.DeferPinComparison(Vector3.zero, .4f) && observer.DeferPinComparison(new Vector3(1001, 20, 0), .4f), "Pin diagnostics were suppressed outside the arriving player's destination.");
        Check(!observer.DeferPinComparison(new Vector3(1001, 20, 0), 2.01f), "Pin comparison quiet period did not expire.");
        observer.Capture(2.1f, 3, 33, 0, 10);
        var line = lines.Single(s => s.Contains("PlayerActivity.arrival"));
        Check(line.Contains("kind=first_character_announced") && line.Contains("max_ms=502.00") && line.Contains("features_ms=480.00") && line.Contains("to=1000.0,20.0,0.0") && line.Contains("pin_comparisons_skipped=2"), "Early activation work, eventual destination or skipped-comparison evidence was lost.");
        var diagnostics = new SharedMapDiagnostics(log, policy);
        diagnostics.PinChanges(2048, actor, 1, new byte[] { 1 }, new byte[] { 2 }, "arrival", defer: true);
        Check(!lines.Any(s => s.Contains("Could not compare table pins")), "Deferred pin diagnostics still decoded malformed evidence.");
        ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: character announcements capture activation before actor availability, retain the eventual destination, and suppress only bounded nearby optional pin comparisons before decoding them.");
    }
}
