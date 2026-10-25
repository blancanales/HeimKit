using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.SharedMap;

internal static class SharedMapStoreTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static SharedMapExploration Map(int size = 64, float pixel = 12) => new(size, pixel, 100, 2);
    public static void Run()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "shared-map-store-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var map = Map();
            var store = new SharedMapStore(directory, 12345, map);
            Check(!store.Load() && !store.Save(), "Empty store unexpectedly wrote a file.");
            map.Reveal(-120, 0);
            Check(store.Save(), "Dirty map did not save.");
            byte[] first = File.ReadAllBytes(store.FilePath);
            var restored = Map(); var reload = new SharedMapStore(directory, 12345, restored);
            Check(reload.Load() && restored.ExportPacked().SequenceEqual(map.ExportPacked()), "Restart lost exploration.");
            Check(!reload.Save(), "Unchanged loaded map rewrote the file.");
            map.Reveal(120, 0); Check(store.Save(), "Second dirty map did not save.");
            Check(File.ReadAllBytes(store.FilePath + ".bak").SequenceEqual(first), "Atomic replacement lost the previous map backup.");
            Check(!new SharedMapStore(directory, 98765, Map()).Load(), "Different world inherited another world's exploration.");

            var empty = new SharedMapStore(directory, 24680, Map());
            Check(!empty.Load() && empty.Save(initialize: true), "Completed empty import did not create its first-run checkpoint.");
            var emptyReload = new SharedMapStore(directory, 24680, Map());
            Check(emptyReload.Load() && !emptyReload.Save(), "Empty checkpoint was treated as first run or rewritten.");

            bool mismatch = false;
            try { new SharedMapStore(directory, 12345, Map(64, 24)).Load(); }
            catch (InvalidDataException) { mismatch = true; }
            Check(mismatch, "Different grid scale reinterpreted old exploration.");

            byte[] valid = File.ReadAllBytes(store.FilePath);
            byte[] corrupt = (byte[])valid.Clone(); corrupt[30] ^= 1;
            File.WriteAllBytes(store.FilePath, corrupt);
            var untouched = Map();
            bool rejected = false;
            try { new SharedMapStore(directory, 12345, untouched).Load(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && untouched.ExploredPixels == 0 && File.ReadAllBytes(store.FilePath).SequenceEqual(corrupt),
                "Corrupt store was imported or replaced silently.");

            File.WriteAllBytes(store.FilePath, valid.Take(valid.Length - 1).ToArray());
            bool truncated = false;
            try { new SharedMapStore(directory, 12345, Map()).Load(); }
            catch (InvalidDataException) { truncated = true; }
            Check(truncated, "Truncated store was accepted.");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "A completed save leaked a temporary file.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: SharedMap world-specific restart persistence, packed terrain, atomic backup, unchanged-save avoidance and corruption/grid rejection.");
    }
}
