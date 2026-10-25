using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapPinRemovalTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static byte[] Table(params (MapPin Pin, bool Checked, string Author)[] pins)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(pins.Length);
            foreach (var (pin, check, author) in pins)
            {
                writer.Write(pin.Owner); writer.Write(pin.Name); writer.Write(pin.X); writer.Write(pin.Y); writer.Write(pin.Z);
                writer.Write(pin.Icon); writer.Write(check); writer.Write(author);
            }
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(data.ToArray());
        return output.ToArray();
    }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-pin-removals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = new MapPin(42, "Vile", 10, 1, 20, 3);
            var elsewhere = new MapPin(42, "Vile", 60, 1, 20, 3);
            var otherOwner = new MapPin(43, "Vile", 10, 1, 20, 3);
            var otherLabel = new MapPin(42, "Home", 10, 1, 20, 3);
            string id = SharedMapPinRemovals.Identity(target);
            Check(SharedMapPinRemovals.ValidId(id) && id != SharedMapPinRemovals.Identity(elsewhere) &&
                id != SharedMapPinRemovals.Identity(otherOwner) && id != SharedMapPinRemovals.Identity(otherLabel), "Removal identity conflated unrelated pins.");
            var store = new SharedMapPinRemovals(directory, 123); store.Load();
            Check(!store.Contains(target) && store.Set(id, true) && !store.Set(id, true), "Removal was not persisted idempotently.");
            var reload = new SharedMapPinRemovals(directory, 123); reload.Load();
            Check(reload.Contains(target) && !reload.Contains(elsewhere), "Restart lost the selected removal or expanded its scope.");
            var map = new SharedMapExploration(64, 12, 50, .25f); map.Reveal(0, 0);
            byte[] original = Table((target, false, "Steam_42"), (elsewhere, true, "Steam_42"), (otherOwner, false, "Steam_43"), (otherLabel, true, "Steam_42"));
            byte[] cleaned = SharedMapTableData.Merge(map, original, out _, removePin: reload.Contains)!;
            var kept = SharedMapTableData.ReadPins(map.Size, cleaned);
            Check(kept.Any(p => p.X == elsewhere.X && p.Checked && p.Author == "Steam_42"), "Pin snapshots lost checked/author metadata.");
            Check(kept.Count == 3 && kept.All(p => SharedMapPinRemovals.Identity(p) != id), "Selected pin survived cleanup or unrelated pins were deleted.");
            byte[] expected = SharedMapTableData.Merge(map, Table((elsewhere, true, "Steam_42"), (otherOwner, false, "Steam_43"), (otherLabel, true, "Steam_42")), out _)!;
            Check(cleaned.SequenceEqual(expected), "Cleanup changed surviving pin fields or explored terrain.");
            Check(SharedMapTableData.Merge(map, cleaned, out _, removePin: reload.Contains) == null, "Unchanged cleaned table was rewritten.");
            byte[] reupload = SharedMapTableData.Merge(map, Table((target, true, "DifferentAuthor"), (elsewhere, true, "Steam_42")), out _, removePin: reload.Contains)!;
            Check(SharedMapTableData.ReadPins(map.Size, reupload).Single().X == elsewhere.X, "Changed check/author evaded a persisted removal.");
            var generated = new MapPin(SharedMapTableData.ResourceOwner, "Obsidian", 1, 2, 3, 3);
            store.Set(SharedMapPinRemovals.Identity(generated), true);
            byte[] noGenerated = SharedMapTableData.Merge(map, original, out _, resources: new[] { new MapMarker("Obsidian", 1, 2, 3) }, removePin: store.Contains)!;
            Check(SharedMapTableData.ReadPins(map.Size, noGenerated).All(p => p.Owner != SharedMapTableData.ResourceOwner), "Selected generated pin bypassed removal.");
            Check(File.Exists(store.FilePath + ".bak"), "Rule updates did not retain a backup.");
            Check(store.Set(id, false) && !store.Contains(target), "Allow failed to remove the exclusion.");
            var allowed = new SharedMapPinRemovals(directory, 123); allowed.Load();
            Check(!allowed.Contains(target), "Allow did not survive restart.");
            byte[] corrupt = File.ReadAllBytes(store.FilePath); corrupt[^1] ^= 1; File.WriteAllBytes(store.FilePath, corrupt);
            try { new SharedMapPinRemovals(directory, 123).Load(); throw new Exception("Corrupt rules were accepted."); } catch (InvalidDataException) { }
            Check(File.ReadAllBytes(store.FilePath).SequenceEqual(corrupt), "Rule loading overwrote corrupt data.");
            var fail = new SharedMapPinRemovals(directory, 456); Directory.CreateDirectory(fail.FilePath);
            try { fail.Set(id, true); throw new Exception("Invalid rule destination was accepted."); } catch (IOException) { }
            Check(!fail.Contains(target), "A failed save activated an unpersisted exclusion.");
            Check(SharedMapPinRemovals.Identity(new MapPin(42, "Vile", 0f, 0f, 0f, 3)) ==
                SharedMapPinRemovals.Identity(new MapPin(42, "Vile", -0f, -0f, -0f, 3)), "Signed zero changed pin identity.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: selected pin cleanup, unrelated/raw pin preservation, repeat uploads, generated pins and persisted bounded rules.");
    }
}
