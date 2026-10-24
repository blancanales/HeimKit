using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapResourceStoreTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SharedMapResourceStore(directory, 123);
            Check(!store.Load(), "Missing resources did not start empty.");
            Check(store.Observe(ResourceKind.Tin, 0, 1, 0, null, true) == null, "Unknown health created a discovery.");
            Check(store.Observe(ResourceKind.Tin, 0, 1, 0, false, false) == null, "A disallowed discovery was created.");
            var first = store.Observe(ResourceKind.Tin, 0, 1, 0, false, true)!;
            store.Observe(ResourceKind.Tin, 20, 1, 0, false, true);
            Check(store.Observe(ResourceKind.Tin, 0, 1, 0, null, false)?.Identity == first.Identity, "Unobserved data lost a discovery.");
            Check(store.Retire(ResourceKind.Tin, 0, 1, 0) && !store.Retire(ResourceKind.Tin, 0, 1, 0), "Retirement was not idempotent.");
            Check(store.Save() && !store.Save(), "Dirty/unchanged saves were not distinguished.");
            var restarted = new SharedMapResourceStore(directory, 123);
            Check(restarted.Load() && restarted.Deposits.Count == 2 && restarted.Deposits[0].Depleted, "Restart lost discoveries or depletion.");
            Check(restarted.Observe(ResourceKind.Tin, 20, 1, 0, false, true)?.Identity == 2 && restarted.Deposits.Count == 2, "Session-independent reassociation duplicated a node.");
            restarted.Observe(ResourceKind.Copper, 40, 1, 0, false, true, new MiningProgress(65, 100)); restarted.Save();
            var progressReload = new SharedMapResourceStore(directory, 123); progressReload.Load();
            Check(progressReload.Deposits[2].Progress?.RemainingSections == 65, "Restart lost Copper progress.");
            progressReload.Observe(ResourceKind.Copper, 40, 1, 0, null, false);
            Check(progressReload.Deposits[2].Progress?.RemainingSections == 65 && !progressReload.Save(), "Unavailable data erased valid Copper progress.");
            Check(File.Exists(store.FilePath + ".bak"), "Atomic save did not retain the prior store.");
            byte[] original = File.ReadAllBytes(store.FilePath), corrupt = (byte[])original.Clone(); corrupt[^1] ^= 1;
            File.WriteAllBytes(store.FilePath, corrupt);
            try { new SharedMapResourceStore(directory, 123).Load(); throw new Exception("Corrupt state was accepted."); }
            catch (InvalidDataException) { }
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(corrupt), "Loading overwrote corrupt state.");
            File.Delete(store.FilePath);
            try { new SharedMapResourceStore(directory, 123).Load(); throw new Exception("Orphaned backup was ignored."); }
            catch (InvalidDataException) { }

            var refresh = new SharedMapMarkerRefresh(directory, 123, resourcePins: true);
            var before = refresh.Apply(new[] { new MapMarker("Tin (2)", 0, 1, 0, "1") })[0]; refresh.Save();
            var changed = refresh.Apply(new[] { new MapMarker("Tin", 0, 1, 0, "1") })[0]; refresh.Save();
            double dx = changed.X - before.X, dz = changed.Z - before.Z;
            Check(dx * dx + dz * dz > 1.21, "Tin count change did not bypass native nearby-pin matching.");
            var reload = new SharedMapMarkerRefresh(directory, 123, resourcePins: true); reload.Load();
            var stable = reload.Apply(new[] { new MapMarker("Tin", 0, 1, 0, "1") })[0];
            Check(stable.X == changed.X && stable.Z == changed.Z, "Restart lost the resource publication slot.");
            Check(refresh.FilePath.EndsWith(".resource-pins") && !File.Exists(Path.ChangeExtension(refresh.FilePath, ".portal-pins")), "Resource publication touched portal storage.");

            var silver = new SharedMapResourceStore(directory, 789);
            Check(silver.Observe(ResourceKind.Silver, 0, 1, 0, false, true, new MiningProgress(8, 10)) == null,
                "Travel or retrospective discovery revealed Silver.");
            var vein = silver.Observe(ResourceKind.Silver, 0, 1, 0, false, true, new MiningProgress(8, 10), true)!;
            Check(vein.SilverDiscovered && silver.Save(), "A qualifying Silver discovery was not saved.");
            var silverReload = new SharedMapResourceStore(directory, 789); silverReload.Load();
            Check(silverReload.Deposits.Single().SilverDiscovered, "Restart lost Silver discovery evidence.");
            Check(silverReload.Observe(ResourceKind.Silver, 0, 1, 0, false, true, new MiningProgress(6, 10), true)?.Identity == vein.Identity &&
                silverReload.Deposits.Count == 1, "Another Silver hit duplicated the saved vein.");
            silverReload.Observe(ResourceKind.Silver, 0, 1, 0, false, true, new MiningProgress(5, 10));
            Check(silverReload.Deposits.Single().Progress?.RemainingSections == 5, "Travel failed to update already discovered Silver.");

            // Format 2 had no Silver discovery evidence; preserve it without publication.
            var oldSilver = new SharedMapResourceStore(directory, 790);
            using var oldBody = new MemoryStream();
            using (var writer = new BinaryWriter(oldBody, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x44524B48u); writer.Write(2); writer.Write(790L); writer.Write(1);
                writer.Write(1); writer.Write((int)ResourceKind.Silver);
                writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(false);
                writer.Write(-1); writer.Write(0);
            }
            byte[] oldBytes = oldBody.ToArray();
            File.WriteAllBytes(oldSilver.FilePath, oldBytes.Concat(System.Security.Cryptography.SHA256.HashData(oldBytes)).ToArray());
            Check(oldSilver.Load() && !oldSilver.Deposits.Single().SilverDiscovered &&
                SharedMapResourceClusters.Group(oldSilver.Deposits, 30, false).Count == 0 && !oldSilver.Save(),
                "Earlier Silver records leaked through historical display or changed merely by loading.");
            Check(oldSilver.Observe(ResourceKind.Silver, 0, 1, 0, false, true) == null, "A scan unlocked earlier Silver.");
            Check(oldSilver.Observe(ResourceKind.Silver, 0, 1, 0, false, true, new MiningProgress(9, 10), true)?.Identity == 1 &&
                oldSilver.Deposits.Count == 1, "A qualifying hit failed to reuse an earlier Silver record.");

            // Earlier discovery files lack section counts. Preserve them until new evidence arrives.
            var legacy = new SharedMapResourceStore(directory, 456);
            using var body = new MemoryStream();
            using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, true))
            {
                writer.Write(0x44524B48u); writer.Write(1); writer.Write(456L); writer.Write(1);
                writer.Write(1); writer.Write((int)ResourceKind.Copper);
                writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(false);
            }
            byte[] legacyBody = body.ToArray();
            File.WriteAllBytes(legacy.FilePath, legacyBody.Concat(System.Security.Cryptography.SHA256.HashData(legacyBody)).ToArray());
            Check(legacy.Load() && legacy.Deposits.Count == 1 && !legacy.Deposits[0].Progress.HasValue && !legacy.Save(),
                "Earlier discoveries were lost, given invented progress or rewritten merely by loading.");
            legacy.Observe(ResourceKind.Copper, 0, 1, 0, false, false, new MiningProgress(60, 100));
            Check(legacy.Save(), "A newly observed percentage did not upgrade the discovery record.");
            var upgraded = new SharedMapResourceStore(directory, 456); upgraded.Load();
            Check(upgraded.Deposits[0].Progress?.RemainingSections == 60 && upgraded.Deposits[0].Identity == 1,
                "Extended discovery storage lost the previous identity or new progress.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("SharedMap resource persistence and publication checks passed.");
    }
}
