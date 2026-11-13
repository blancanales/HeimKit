using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HeimKit.Core.Native;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

internal static class SharedMapAncientRootTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { NativeObservations(); PersistenceAndDisplay(); }
    private static SharedMapResourcePrefab Metadata()
        => (SharedMapResourcePrefab)typeof(SharedMapResourcePrefab).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { "YggdrasilRoot", ResourceKind.AncientRoot, false, 1, false, false, float.NaN, 50f });
    private static void NativeObservations()
    {
        Check((int)ResourceKind.BlackMarble == 5 && (int)ResourceKind.AncientRoot == 6, "Ancient Roots renumbered prior stored resource kinds.");
        var catalog = (ValueTuple<string, ResourceKind, string, string?>[])typeof(SharedMapResources)
            .GetField("Catalog", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(catalog.Count(c => c.Item2 == ResourceKind.AncientRoot) == 1 &&
            catalog.Single(c => c.Item2 == ResourceKind.AncientRoot) == ("YggdrasilRoot", ResourceKind.AncientRoot, "Sap", null),
            "The root allowlist included collectors, ordinary roots or unsupported sources.");
        Check(SharedMapAncientRoot.Percentage(50, 50) == 100 && SharedMapAncientRoot.Percentage(32.9f, 50) == 65 &&
            SharedMapAncientRoot.Percentage(0, 50) == 0 && SharedMapAncientRoot.Percentage(.1f, 50) == 0,
            "Root reserve did not use the native maximum or floor whole percentages.");
        foreach (var (level, maximum) in new[] { (float.NaN, 50f), (float.PositiveInfinity, 50f), (-1f, 50f), (51f, 50f), (1f, 0f), (1f, float.NaN) })
            Check(SharedMapAncientRoot.Percentage(level, maximum) == null, "Invalid reserve/capacity invented a sap percentage.");
        using var world = new ReviewRegressionTests.World(); var record = world.Add(33001);
        var metadata = Metadata();
        Check(metadata.SapPercentage(record) == 100, "Absent native level disagreed with ResourceRoot.GetLevel's maximum default.");
        uint revision = record.DataRevision;
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, 0f);
        for (int i = 0; i < 100; i++) Check(metadata.SapPercentage(record) == 0 && metadata.Depleted(record) == false && metadata.Progress(record) == null,
            "An empty regenerating root was treated as a mined/depleted resource.");
        Check(record.GetFloat(ZDOVars.s_level) == 0 && record.DataRevision == revision && record.GetLong(ZDOVars.s_lastTime) == 0,
            "Reading reserve simulated regeneration, changed time, or mutated the native record.");
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, 40f);
        Check(metadata.SapPercentage(record) == 80, "A later native regeneration observation did not increase reserve.");
        System.Console.WriteLine("PASS: discovered-root metadata uses the native sap maximum/default, rejects invalid levels, keeps empty roots renewable, and never mutates level/time/revisions or simulates regeneration.");
    }
    private static void PersistenceAndDisplay()
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(temp, "heimkit-ancient-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SharedMapResourceStore(directory, 12345);
            store.Observe(ResourceKind.Copper, 500, 1, 0, false, true, new MiningProgress(2, 4));
            store.Save();
            Check(BitConverter.ToInt32(File.ReadAllBytes(store.FilePath), 4) == 3, "Mineral-only saves needlessly forced a root schema migration.");
            var loaded = new SharedMapResourceStore(directory, 12345); loaded.Load();
            Check(loaded.Deposits.Single().Progress?.RemainingSections == 2 && !loaded.Save(), "Legacy mineral data was changed merely by loading.");
            using var world = new ReviewRegressionTests.World(); var record = world.Add(33002);
            ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, 0f);
            var resources = (SharedMapResources)RuntimeHelpers.GetUninitializedObject(typeof(SharedMapResources));
            typeof(SharedMapResources).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resources, loaded);
            var observe = typeof(SharedMapResources).GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var metadata = Metadata();
            // Exercise the actual controller observation path: reserve=0 must still
            // establish a valid discovered root, rather than being lost as unknown depletion.
            observe.Invoke(resources, new object?[] { record, metadata, record.GetPosition(), false, null });
            Check(loaded.Deposits.Last().Kind == ResourceKind.AncientRoot && loaded.Deposits.Last().SapPercent == 0 && !loaded.Deposits.Last().Depleted,
                "The real resource observation path failed to discover an empty root.");
            var root = loaded.Deposits.Last();
            Check(SharedMapResourceClusters.Group(loaded.Deposits, 30, true).Any(m => m.Name == "Ancient Root 0%"),
                "ResourceRemoveDepleted hid a temporarily empty root.");
            ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, 32.5f);
            observe.Invoke(resources, new object?[] { record, metadata, record.GetPosition(), false, null });
            Check(root.SapPercent == 65 && loaded.Deposits.Count == 2, "Regeneration duplicated a discovery or failed to update the last observed level.");
            long before = loaded.Revision;
            ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, 32.6f);
            observe.Invoke(resources, new object?[] { record, metadata, record.GetPosition(), false, null });
            Check(loaded.Revision == before, "An unchanged displayed percentage dirtied storage/publication every sample.");
            ZDOExtraData.Set(record.m_uid, ZDOVars.s_level, float.NaN);
            observe.Invoke(resources, new object?[] { record, metadata, record.GetPosition(), false, null });
            Check(root.SapPercent == 65, "Malformed data erased the last accepted reserve.");
            loaded.Observe(ResourceKind.AncientRoot, 2, 1, 0, false, true);
            var markers = SharedMapResourceClusters.Group(loaded.Deposits, 100, true);
            Check(markers.Count(m => m.Name.StartsWith("Ancient Root")) == 2 && markers.Any(m => m.Name == "Ancient Root ?%"),
                "Nearby roots were merged or unknown reserve was invented.");
            loaded.Save();
            Check(BitConverter.ToInt32(File.ReadAllBytes(loaded.FilePath), 4) == 4, "Root reserve was written without the extended store schema.");
            var reloaded = new SharedMapResourceStore(directory, 12345); reloaded.Load();
            Check(reloaded.Deposits.Count == 3 && reloaded.Deposits[1].SapPercent == 65 && reloaded.Deposits[2].SapPercent == null &&
                reloaded.Deposits[0].Progress?.RemainingSections == 2, "Restart lost root reserve/discovery or changed old mining evidence.");
            var map = new SharedMapExploration(64, 12, 50, .3f);
            byte[] payload = SharedMapTableData.Merge(map, null, out _, resources: markers)!;
            Check(SharedMapTableData.ReadPins(64, payload).Count(p => p.Owner == SharedMapTableData.ResourceOwner && p.Icon == 3 && p.Name.StartsWith("Ancient Root")) == 2,
                "Discovered root markers did not publish as ordinary native resource dots.");
            Check(reloaded.Retire(ResourceKind.AncientRoot, 0, 0, 0) &&
                !SharedMapResourceClusters.Group(reloaded.Deposits, 100, true).Any(m => m.Identity == root.Identity.ToString()) &&
                SharedMapResourceClusters.Group(reloaded.Deposits, 100, false).Any(m => m.Name == "Ancient Root (removed)"),
                "Actual terminal removal did not retire a root or historical display confused destruction with empty reserve.");
            // Re-sign an invalid percentage so schema validation, not checksum
            // rejection, must protect the loaded store and its accepted records.
            byte[] bytes = File.ReadAllBytes(loaded.FilePath); int bodyLength = bytes.Length - 32;
            BitConverter.GetBytes(101).CopyTo(bytes, 20 + 34 + 30);
            SHA256.HashData(bytes.AsSpan(0, bodyLength)).CopyTo(bytes.AsSpan(bodyLength));
            File.WriteAllBytes(loaded.FilePath, bytes);
            bool rejected = false; try { reloaded.Load(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && reloaded.Deposits.Count == 3, "A forged out-of-range reserve was accepted or replaced the prior loaded state.");
            System.Console.WriteLine("PASS: empty/unknown roots are discovered, remain separate and persist across restart; native refill updates deduplicate; removal, native pins, legacy schemas and corrupt-reserve rejection retain their contracts.");
        }
        finally
        {
            string relative = Path.GetRelativePath(temp, Path.GetFullPath(directory));
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) &&
                Path.GetFileName(directory).StartsWith("heimkit-ancient-roots-", StringComparison.Ordinal) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
