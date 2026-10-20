using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapDungeonPinTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static MapMarker Marker(string id, float x, float z, string name = "Burial Chamber") => new(name, x, 55, z, id);
    private static byte[] Table(params MapPin[] pins)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(pins.Length);
            foreach (var p in pins)
            { writer.Write(p.Owner); writer.Write(p.Name); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Icon); writer.Write(p.Checked); writer.Write(p.Author); }
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) raw.WriteTo(gzip);
        return output.ToArray();
    }
    public static void Run()
    {
        var nearby = new[] { Marker("a", 0, 0), Marker("b", 10, 0, "Troll Cave"), Marker("c", 20, 0) };
        var grouped = new SharedMapDungeonPins(nearby);
        Check(grouped.Markers.Select(m => m.Identity).SequenceEqual(new[] { "a", "c" }) && grouped.Suppressed == 1,
            "Ten-metre cleanup failed its inclusive boundary or absorbed a distant source through a chain.");
        Check(new SharedMapDungeonPins(nearby.Reverse().ToArray()).Markers.Select(m => m.Identity).SequenceEqual(new[] { "a", "c" }),
            "Upload/source order changed the canonical entrance.");
        Check(new SharedMapDungeonPins(new[] { Marker("a", 0, 0), Marker("b", 10.01f, 0) }).Markers.Count == 2,
            "Cleanup removed a dungeon outside ten metres.");
        Check(new SharedMapDungeonPins(new[] { Marker("a", -10.01f, -10), Marker("b", -9.99f, -10) }).Markers.Count == 1,
            "A negative-coordinate grid boundary hid a duplicate.");
        Check(SharedMapDungeonPins.Family("Burial Chamber 0%") == "Burial Chamber" &&
            SharedMapDungeonPins.Family("burial chamber 100%") == "Burial Chamber" &&
            SharedMapDungeonPins.Family("troll Cave") == "Troll Cave" && SharedMapDungeonPins.Family("Burial Chamber 101%") == null &&
            SharedMapDungeonPins.Family("Burial Chamber supplies") == null && SharedMapDungeonPins.Family("Home") == null,
            "Dungeon label recognition accepted arbitrary notes or missed an older percentage label.");
        // Log-derived old and new publication points for the same dungeon, plus an unrelated local note.
        var markers = new[] { Marker("stable-source", 520.591858f, 441.392029f, "Burial Chamber 0%") };
        var old = new MapPin(2473943127, "Burial Chamber 100%", 522.591858f, 56.3352432f, 441.392029f, 2, true, "Steam_test");
        var adopted = new MapPin(2473943127, "Burial Chamber 0%", 522.591858f, 56.3352432f, 443.392029f, 2, true, "Steam_test");
        var note = new MapPin(77, "tomb", old.X, 0, old.Z, 2, true, "Steam_77");
        var dot = new MapPin(78, "Burial Chamber", old.X, 0, old.Z, 3);
        var distant = new MapPin(79, "Burial Chamber 100%", 600, 0, old.Z, 2);
        var map = new SharedMapExploration(64, 12, 50, .25f);
        var upload = Table(old, adopted, note, dot, distant);
        int obsolete = 0;
        var merged = SharedMapTableData.Merge(map, upload, out _, dungeons: markers, dungeonCleanup: (_, pins) => obsolete = pins)!;
        var pins = SharedMapTableData.ReadPins(64, merged);
        Check(obsolete == 2 && pins.Count(p => p.Owner == SharedMapTableData.DungeonOwner) == 1 &&
            pins.Count == 4 && pins.Any(p => p.Owner == 77 && p.Checked && p.Author == "Steam_77") &&
            pins.Any(p => p.Owner == 78) && pins.Any(p => p.Owner == 79),
            "Stale dungeon copies survived or unrelated notes/dots/distant markers were changed.");
        Check(SharedMapTableData.Merge(map, merged, out _, dungeons: markers) == null, "Clean dungeon publications rewrote unchanged table data.");
        var reuploaded = SharedMapTableData.Merge(map, upload, out _, dungeons: markers)!;
        Check(reuploaded.SequenceEqual(merged), "A stale player upload resurrected duplicate dungeon publications.");
        var noCatalog = SharedMapTableData.Merge(map, upload, out _, dungeons: Array.Empty<MapMarker>());
        Check(noCatalog == null, "Disabling dungeon discovery broadly deleted player dungeon pins without an accepted source.");
        var exclusion = SharedMapTableData.Merge(map, upload, out _, dungeons: nearby,
            removePin: p => p.Owner == SharedMapTableData.DungeonOwner && p.X == 0)!;
        Check(SharedMapTableData.ReadPins(64, exclusion).Where(p => p.Owner == SharedMapTableData.DungeonOwner).Single().X == 20,
            "A suppressed nearby source replaced a selected excluded anchor.");
        System.Console.WriteLine("PASS: ten-metre dungeon publication cleanup, deterministic anchors, stale percentage/adopted copies, repeat uploads and unrelated-pin preservation.");
    }
}
