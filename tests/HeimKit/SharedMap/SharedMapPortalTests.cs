using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapPortalTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(data);
        return output.ToArray();
    }
    private static byte[] Raw(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
    }
    private static List<(long Owner, string Name, float X, int Type)> Pins(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(Raw(data)));
        int version = reader.ReadInt32(), pixels = reader.ReadInt32(); reader.ReadBytes(pixels);
        var pins = new List<(long, string, float, int)>();
        if (version == 1) return pins;
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            long owner = reader.ReadInt64(); string name = reader.ReadString(); float x = reader.ReadSingle();
            reader.ReadSingle(); reader.ReadSingle(); int type = reader.ReadInt32(); reader.ReadBoolean();
            if (version >= 3) reader.ReadString();
            pins.Add((owner, name, x, type));
        }
        Check(reader.BaseStream.Position == reader.BaseStream.Length, "Portal payload contains trailing bytes.");
        return pins;
    }
    private static void Pin(BinaryWriter writer, int version, long owner, string name, float x, int type = 0)
    {
        writer.Write(owner); writer.Write(name); writer.Write(x); writer.Write(0f); writer.Write(0f);
        writer.Write(type); writer.Write(true); if (version >= 3) writer.Write("Steam_123");
    }
    public static void Run()
    {
        var map = new SharedMapExploration(64, 12, 100, 2); map.Reveal(0, 0);
        var portals = new[] { new MapMarker("Home", 0, 0, 0), new MapMarker("Home", 240, 0, 0) };
        byte[] first = SharedMapTableData.Merge(map, null, out _, portals, true)!;
        Check(Pins(first).Count == 2 && Pins(first).All(p => p.Owner == SharedMapTableData.PortalOwner), "Same-tag distant endpoints were lost or given a player owner.");
        Check(Pins(first).All(p => p.Name == "Home" && p.Type == 6), "Generated portals used a text prefix or the wrong native icon.");
        Check(SharedMapTableData.Merge(map, first, out _, portals, true) == null, "Unchanged portal data was rewritten.");
        byte[] renamed = SharedMapTableData.Merge(map, first, out _, new[] { new MapMarker("Farm", 12, 0, 0) }, true)!;
        Check(Pins(renamed).Single().Name == "Farm" && Pins(renamed).Single().X == 12, "Rename/movement retained old markers.");
        byte[] disabled = SharedMapTableData.Merge(map, renamed, out _, Array.Empty<MapMarker>())!;
        Check(Pins(disabled).Count == 0 && BitConverter.ToInt32(Raw(disabled), 0) >= 2, "Removed portals did not produce a native pin-clear payload.");
        Check(SharedMapTableData.Merge(map, disabled, out _, Array.Empty<MapMarker>()) == null, "Disabled portal cleanup was not idempotent.");

        foreach (int version in new[] { 2, 3 })
        {
            using var source = new MemoryStream();
            byte[] kept;
            using (var writer = new BinaryWriter(source, Encoding.UTF8, true))
            {
                writer.Write(version); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(5);
                int start = (int)source.Position; Pin(writer, version, 42, "[Portal] Personal note", 500);
                kept = source.ToArray().Skip(start).ToArray();
                Pin(writer, version, 42, "Wrong portal name", 999, 6);
                Pin(writer, version, SharedMapTableData.PortalOwner, "Old", 888);
                Pin(writer, version, SharedMapTableData.PortalOwner, "Duplicate", 888);
                Pin(writer, version, 42, "Death", 0, 4);
            }
            var output = SharedMapTableData.Merge(map, Compress(source.ToArray()), out int deaths, portals, true)!;
            var pins = Pins(output);
            Check(deaths == 1 && pins.Count == 3 && pins.Count(p => p.Owner == SharedMapTableData.PortalOwner) == 2,
                "Stale manual/generated markers, duplicates or death were retained.");
            Check(Raw(output).Skip(4108).Take(kept.Length).SequenceEqual(kept), "Unrelated personal pin bytes changed.");
            Check(BitConverter.ToInt32(Raw(output), 0) == version, "Existing native pin version was unnecessarily upgraded.");
            Check(SharedMapTableData.Merge(map, output, out _, portals, true) == null, "Stale-upload normalization was not idempotent.");
            var off = SharedMapTableData.Merge(map, Compress(source.ToArray()), out _, Array.Empty<MapMarker>())!;
            Check(Pins(off).Any(p => p.Name == "Wrong portal name" && p.Type == 6) && Pins(off).All(p => p.Owner != SharedMapTableData.PortalOwner),
                "Disabled automatic portals erased player-owned portal icons or retained generated pins.");
        }

        using (var source = new MemoryStream())
        {
            using (var writer = new BinaryWriter(source, Encoding.UTF8, true))
            {
                writer.Write(3); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(3);
                Pin(writer, 3, 42, "home", 0, 3);
                Pin(writer, 3, 42, "Home", 100, 3);
                Pin(writer, 3, 42, "Other", 0, 3);
            }
            byte[] stale = Compress(source.ToArray());
            int cleaned = 0;
            var fixedData = SharedMapTableData.Merge(map, stale, out _, portals, true,
                (_, _, _, _, _, removed) => { if (removed) cleaned++; })!;
            Check(cleaned == 1 && !Pins(fixedData).Any(p => p.Name == "home"), "Matching nearby stale dot was retained.");
            Check(Pins(fixedData).Any(p => p.Name == "Home" && p.X == 100 && p.Type == 3) &&
                Pins(fixedData).Any(p => p.Name == "Other"), "Distant same-name or unrelated nearby pin was removed.");
            Check(SharedMapTableData.Merge(map, fixedData, out _, portals, true) == null, "Stale-dot cleanup was not stable.");
            var off = SharedMapTableData.Merge(map, stale, out _, portals, false)!;
            Check(Pins(off).Any(p => p.Name == "home"), "Disabled portal management removed a legacy dot.");
        }

        var near = new[] { new MapMarker("A", 0, 0, 0), new MapMarker("B", 20, 2, 0), new MapMarker("C", 40, 0, 0) };
        var grouped = SharedMapPortalClusters.Group(near, 20);
        Check(grouped.Count == 2 && grouped[0].Name == "Multi Portals (2)" && grouped[0].X == 10 && grouped[0].Y == 1,
            "Boundary/centroid grouping failed or a transitive chain joined distant portals.");
        Check(grouped[1].Name == "C" && SharedMapPortalClusters.Group(near, 0).Count == 3, "Single portal names or disabled grouping changed.");
        Check(SharedMapPortalClusters.Group(Array.Empty<MapMarker>(), 20).Count == 0, "Empty portal catalog generated a marker.");
        byte[] clustered = SharedMapTableData.Merge(map, first, out _, grouped, true)!;
        Check(Pins(clustered).Any(p => p.Name == "Multi Portals (2)" && p.Type == 6), "Group label or native portal icon was incorrect.");
        System.Console.WriteLine("PASS: managed portal add/rename/move/removal, portal-icon cleanup, duplicate/death filtering, other-icon pin preservation, native formats, idempotence and bounded-anchor grouping.");
    }
}
