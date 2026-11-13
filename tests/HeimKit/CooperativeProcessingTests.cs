using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using HeimKit.Core.Scheduling;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;
using HeimKit.Features.WeatherGuard;
using HeimKit.Core.Native;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class CooperativeProcessingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { Budgets(); TablePublication(); RejectionAndCancellation(); ReusedWeatherRecords(); }
    private static void Budgets()
    {
        int progressed = 0, disposed = 0;
        IEnumerable<bool> Work()
        {
            try { while (progressed < 1000) { progressed++; yield return true; } }
            finally { disposed++; }
        }
        using var work = Work().GetEnumerator();
        Check(!WorkSlice.Advance(work, maximumUnits: 17, clock: () => 0) && progressed == 17,
            "A cheap slice exceeded its unit limit.");
        long clock = 0;
        Check(!WorkSlice.Advance(work, clock: () => clock += Stopwatch.Frequency) && progressed == 18,
            "An expensive unit did not yield when the elapsed budget expired.");
        // Weather protection is mandatory work: slicing must not impose a combat stop.
        ModuleScheduler.Current.Combat.SetRaid(true);
        try { Check(!WorkSlice.Advance(work, maximumUnits: 2, clock: () => 0) && progressed == 20, "A required slice lost progress during a raid."); }
        finally { ModuleScheduler.Current.Combat.Clear(); }
        work.Dispose(); Check(disposed == 1, "Cancelling a slice did not run iterator cleanup exactly once.");
        System.Console.WriteLine("PASS: cooperative work enforces elapsed and unit limits, retains mandatory progress during raids, and disposes suspended work.");
    }
    private static byte[] Payload(int size, int version = 3, bool invalidTerrain = false, bool trailing = false)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(version); writer.Write(size * size);
            var terrain = new byte[size * size]; terrain[0] = 1;
            if (invalidTerrain) terrain[^1] = 2;
            writer.Write(terrain);
            if (version >= 2)
            {
                writer.Write(2);
                foreach (var pin in new[] { ("Personal", 1), ("Death", (int)Minimap.PinType.Death) })
                {
                    writer.Write(42L); writer.Write(pin.Item1); writer.Write(1f); writer.Write(2f); writer.Write(3f);
                    writer.Write(pin.Item2); writer.Write(true); if (version >= 3) writer.Write("author");
                }
            }
            if (trailing) writer.Write((byte)99);
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw.ToArray());
        return output.ToArray();
    }
    private static SharedMapReconciliationState Inputs(SharedMapExploration map, IReadOnlyList<MapMarker>? portals = null)
        => new(map.ExploredPixels, 0, 0, 0, 10, portals ?? Array.Empty<MapMarker>(), Array.Empty<MapMarker>(),
            Array.Empty<MapMarker>(), Array.Empty<MapMarker>(), Array.Empty<MapMarker>(), Array.Empty<MapMarker>());
    private static void TablePublication()
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            const int size = 2048;
            var map = new SharedMapExploration(size, 12, 50, .3f); map.Reveal(0, 0);
            byte[] source = Payload(size, version), original = (byte[])source.Clone();
            var markers = new[] { new MapMarker("Portal", 100, 2, 100, "portal:test") };
            using var work = new SharedMapTableData.Reconciliation(map, source, Inputs(map, markers), true, null, null, null, null);
            Check(!work.CanPublish(source), "An unprocessed table became publishable.");
            // Native storage may replace/mutate its buffer while this job is suspended.
            source[0] ^= 1;
            Check(work.SourceMatches(original) && !work.SourceMatches(source), "A mutable input changed the captured source or escaped stale-write detection.");
            int slices = 0;
            while (!work.Advance())
            {
                Check(!work.CanPublish(original), "A partial compressed stream became publishable.");
                Check(++slices < 10000, "The periodic table pipeline stopped making progress.");
            }
            Check(slices > 1 && work.CanPublish(original) && !work.CanPublish(source), "A full-sized table did not yield or accepted a newer field at publication.");
            var pins = SharedMapTableData.ReadPins(size, work.Output);
            Check(pins.Count == (version == 1 ? 1 : 2) && pins.Any(p => p.Name == "Portal" && p.Owner == SharedMapTableData.PortalOwner),
                "Sliced output lost generated markers or retained a death pin.");
            if (version >= 2) Check(pins.Any(p => p.Name == "Personal" && p.Checked && p.Owner == 42 && p.Author == (version >= 3 ? "author" : "")),
                "A sliced reconciliation changed a personal pin, author, owner or checkmark.");
            Check(map.IsExploredPixel(0, 0) && map.IsExplored(0, 0) && work.RemovedDeaths == (version == 1 ? 0 : 1),
                "Sliced terrain union or death accounting changed semantics.");
            work.Dispose(); Check(!work.CanPublish(original), "Cancelled/disposed work remained publishable.");
        }
        System.Console.WriteLine("PASS: full-sized native table versions yield across updates, preserve terrain/personal pins and publish only complete output against an unchanged captured source.");
    }
    private static void RejectionAndCancellation()
    {
        foreach (var source in new[] { Payload(2048, invalidTerrain: true), Payload(16, trailing: true), new byte[] { 1, 2, 3 } })
        {
            int size = source.Length > 1000 ? 2048 : 16;
            var map = new SharedMapExploration(size, 12, 50, .3f);
            int published = 0;
            using var work = new SharedMapTableData.Reconciliation(map, source, Inputs(map), true, null, null,
                (pin, _) => { published++; return pin; }, null);
            bool rejected = false;
            try { for (int i = 0; i < 10000 && !work.Advance(); i++) { } }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { rejected = true; }
            Check(rejected && published == 0 && map.ExploredPixels == 0 && !work.CanPublish(source),
                "Malformed or trailing data reached publication or imported terrain before full validation.");
        }
        for (int repeat = 0; repeat < 4; repeat++)
        {
            var map = new SharedMapExploration(2048, 12, 50, .3f);
            using var cancelled = new SharedMapTableData.Reconciliation(map, Payload(2048), Inputs(map), false, null, null, null, null);
            Check(!cancelled.Advance(), "A large decoder did not suspend for cancellation.");
            cancelled.Dispose(); cancelled.Dispose();
            Check(map.ExploredPixels == 0, "Cancellation before validation imported untrusted terrain.");
        }
        System.Console.WriteLine("PASS: malformed/trailing tables are rejected before pin callbacks or terrain import; repeated cancellation safely releases suspended decoder resources.");
    }
    private static void ReusedWeatherRecords()
    {
        using var world = new ReviewRegressionTests.World();
        var record = world.Add(987); record.DataRevision = 19;
        var oldIdentity = new ZDOID(71, 986);
        var protections = new Dictionary<ZDOID, ZDO> { [oldIdentity] = record };
        var controller = (WeatherGuardController)RuntimeHelpers.GetUninitializedObject(typeof(WeatherGuardController));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WeatherGuardController).GetField("_protected", fields)!.SetValue(controller, protections);
        typeof(WeatherGuardController).GetField("_manager", fields)!.SetValue(controller, world.Manager);
        var release = NativeAccess.Method<Action<WeatherGuardController, ZDOID>>(typeof(WeatherGuardController), "Release", typeof(ZDOID));
        release(controller, oldIdentity);
        Check(protections.Count == 0 && record.DataRevision == 19 && world.Manager.GetZDO(record.m_uid) == record,
            "Releasing suspended protection changed a pooled record reused by another native identity.");
        var stale = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); stale.m_uid = record.m_uid;
        NativeAccess.Field<ZDO, int>("m_prefab")(stale) = 7;
        protections.Add(record.m_uid, stale); release(controller, record.m_uid);
        Check(protections.Count == 0 && record.DataRevision == 19 && ReferenceEquals(world.Manager.GetZDO(record.m_uid), record),
            "Releasing stale protection changed a replacement native record with the same identity.");
        System.Console.WriteLine("PASS: releasing suspended WeatherGuard state never changes a pooled/replacement native record after its captured identity or binding changes.");
    }
}
