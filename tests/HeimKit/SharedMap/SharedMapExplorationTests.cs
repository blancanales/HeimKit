using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;

internal static class SharedMapExplorationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw);
        return output.ToArray();
    }
    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output); return output.ToArray();
    }
    public static void Run()
    {
        var map = new SharedMapExploration(64, 12, 100, 2);
        Check(map.Reveal(0, 0) > 0 && map.IsExploredPixel(41, 32), "Native radius must ceil to nine pixels.");
        Check(!map.IsExploredPixel(42, 32) && !map.IsExploredPixel(41, 33), "Pixel circle leaked beyond native boundary.");
        Check(map.Reveal(0, 0) == 0 && map.Reveal(float.NaN, 0) == 0 && map.Reveal(999999, 0) == 0,
            "Repeated or invalid observations changed exploration.");
        var endpoints = new SharedMapExploration(256, 12, 12, 2);
        endpoints.Reveal(-600, 0); endpoints.Reveal(600, 0);
        Check(!endpoints.IsExplored(0, 0), "Teleport endpoints revealed a connecting corridor.");
        var rounding = new SharedMapExploration(64, 12, 1, 2);
        rounding.Reveal(-6, -6);
        Check(rounding.IsExploredPixel(33, 32) && !rounding.IsExploredPixel(30, 32), "Native half-pixel rounding changed.");

        byte[] first = SharedMapTableData.Merge(map, null)!;
        byte[] rawFirst = Decompress(first);
        using (var reader = new BinaryReader(new MemoryStream(rawFirst)))
        {
            Check(reader.ReadInt32() == 1 && reader.ReadInt32() == 4096, "Fresh table is not native exploration-only data.");
            Check(reader.ReadBytes(4096).Count(b => b == 1) == map.ExploredPixels, "Native boolean pixels differ from the mask.");
            Check(reader.BaseStream.Position == reader.BaseStream.Length, "Fresh table contains unexpected pins or trailing bytes.");
        }
        Check(SharedMapTableData.Merge(map, first) == null, "Unchanged terrain caused a redundant request.");

        foreach (int version in new[] { 2, 3 })
        {
            using var source = new MemoryStream();
            using (var writer = new BinaryWriter(source, Encoding.UTF8, true))
            {
                writer.Write(version); writer.Write(4096);
                var pixels = new byte[4096]; pixels[0] = 1; writer.Write(pixels);
                writer.Write(1); writer.Write(8123456789L); writer.Write("Personal pin: café");
                writer.Write(24f); writer.Write(3f); writer.Write(-12f); writer.Write(0); writer.Write(true);
                if (version == 3) writer.Write("Steam_123");
            }
            byte[] original = source.ToArray();
            byte[] merged = Decompress(SharedMapTableData.Merge(map, Compress(original))!);
            Check(merged[8] == 1 && merged.Skip(4104).SequenceEqual(original.Skip(4104)), "Existing exploration or pin bytes were changed.");
            Check(BitConverter.ToInt32(merged, 0) == version, "Existing table version was rewritten.");
            Check(map.IsExploredPixel(0, 0), "Accepted table terrain was not imported into the community map.");
        }
        Check(SharedMapTableData.Merge(new SharedMapExploration(64, 12, 100, 2), Array.Empty<byte>()) == null,
            "A new empty table should remain empty until exploration exists.");
        foreach (byte[] invalid in new[] { new byte[8], new byte[] { 1, 2, 3 } })
        {
            bool rejected = false;
            try { SharedMapTableData.Merge(map, Compress(invalid)); }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException) { rejected = true; }
            Check(rejected, "Malformed table was accepted.");
        }
        var bad = (byte[])rawFirst.Clone(); bad[8] = 2;
        bool badBoolean = false;
        try { SharedMapTableData.Merge(map, Compress(bad)); } catch (InvalidDataException) { badBoolean = true; }
        Check(badBoolean, "Non-native boolean accepted.");
        bad = (byte[])rawFirst.Clone(); bad[0] = 4;
        bool badVersion = false;
        try { SharedMapTableData.Merge(map, Compress(bad)); } catch (InvalidDataException) { badVersion = true; }
        Check(badVersion, "Future table version was rewritten.");
        DeathFiltering();
        StartupImport();
        System.Console.WriteLine("PASS: SharedMap native pixel boundaries, endpoint-only exploration, terrain-only native payload, monotonic merge, opaque pin preservation and malformed-table rejection.");
    }

    private static void DeathFiltering()
    {
        foreach (int version in new[] { 2, 3 })
        {
            var map = new SharedMapExploration(64, 12, 100, 2);
            using var data = new MemoryStream();
            byte[] kept;
            using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
            {
                writer.Write(version); writer.Write(4096);
                var terrain = new byte[4096]; terrain[0] = 1; writer.Write(terrain); writer.Write(2);
                long start = data.Position;
                WritePin(writer, version, 0, "Personal pin", 918273645L);
                long end = data.Position;
                kept = data.ToArray().Skip((int)start).Take((int)(end - start)).ToArray();
                WritePin(writer, version, 4, "Death", 8712345L);
            }
            var normalized = SharedMapTableData.Merge(map, Compress(data.ToArray()), out int removed)!;
            byte[] raw = Decompress(normalized);
            Check(removed == 1 && BitConverter.ToInt32(raw, 4104) == 1, "Death pin was not removed independently of exploration updates.");
            Check(raw.Skip(4108).SequenceEqual(kept), "Surviving personal pin identity or bytes were changed.");
            Check(map.IsExploredPixel(0, 0), "Valid uploaded exploration was not retained.");
            Check(SharedMapTableData.Merge(map, normalized, out int again) == null && again == 0, "Normalization was not idempotent.");

            byte[] invalid = data.ToArray().Concat(new byte[] { 123 }).ToArray();
            var pristine = new SharedMapExploration(64, 12, 100, 2);
            try { SharedMapTableData.Merge(pristine, Compress(invalid)); throw new Exception("Trailing payload was accepted."); }
            catch (InvalidDataException) { Check(pristine.ExploredPixels == 0, "Rejected payload partially imported terrain."); }
        }
    }

    private static void StartupImport()
    {
        var community = new SharedMapExploration(64, 12, 100, 2);
        var first = new SharedMapExploration(64, 12, 100, 2);
        var second = new SharedMapExploration(64, 12, 100, 2);
        first.Reveal(-240, 0); second.Reveal(240, 0);
        byte[] firstTable = SharedMapTableData.Merge(first, null)!;
        byte[] original = (byte[])firstTable.Clone();
        SharedMapTableData.Import(community, firstTable);
        SharedMapTableData.Import(community, SharedMapTableData.Merge(second, null)!);
        Check(community.IsExplored(-240, 0) && community.IsExplored(240, 0), "Startup import lost terrain from one table.");
        Check(firstTable.SequenceEqual(original), "Read-only startup import changed its input table.");
        int pixels = community.ExploredPixels;
        SharedMapTableData.Import(community, firstTable);
        Check(community.ExploredPixels == pixels, "Repeated table import changed the union.");
        var rejected = new SharedMapExploration(64, 12, 100, 2);
        byte[] invalid = Decompress(firstTable).Concat(new byte[] { 123 }).ToArray();
        try { SharedMapTableData.Import(rejected, Compress(invalid)); throw new Exception("Invalid startup table accepted."); }
        catch (InvalidDataException) { Check(rejected.ExploredPixels == 0, "Invalid startup table partially imported terrain."); }
    }

    private static void WritePin(BinaryWriter writer, int version, int type, string name, long owner)
    {
        writer.Write(owner); writer.Write(name);
        writer.Write(24f); writer.Write(3f); writer.Write(-12f); writer.Write(type); writer.Write(true);
        if (version == 3) writer.Write("Steam_123");
    }
}
