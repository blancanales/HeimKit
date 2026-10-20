using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapResourceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Health(params float[] values)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(values.Length); foreach (float value in values) writer.Write(value);
        return Convert.ToBase64String(stream.ToArray());
    }
    private static List<(long Owner, string Name, int Type, byte[] Raw)> Pins(byte[] compressed)
    {
        using var input = new MemoryStream(compressed); using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); gzip.CopyTo(output); byte[] raw = output.ToArray();
        using var reader = new BinaryReader(new MemoryStream(raw));
        int version = reader.ReadInt32(), pixels = reader.ReadInt32(); reader.ReadBytes(pixels);
        var pins = new List<(long, string, int, byte[])>();
        if (version == 1) return pins;
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int start = (int)reader.BaseStream.Position;
            long owner = reader.ReadInt64(); string name = reader.ReadString();
            reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); int type = reader.ReadInt32();
            reader.ReadBoolean(); if (version >= 3) reader.ReadString();
            pins.Add((owner, name, type, raw[start..(int)reader.BaseStream.Position]));
        }
        return pins;
    }
    private static byte[] PersonalPin()
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(1);
            writer.Write(42L); writer.Write("Tin"); writer.Write(0f); writer.Write(1f); writer.Write(0f);
            writer.Write(3); writer.Write(true); writer.Write("Steam_123");
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw.ToArray());
        return output.ToArray();
    }

    public static void Run()
    {
        Check(SharedMapResourceHealth.Sections("", 2) == false, "Untouched mining sections were treated as depleted.");
        Check(SharedMapResourceHealth.Sections(Health(0, -1), 2) == true, "Complete depletion was lost.");
        Check(SharedMapResourceHealth.Sections(Health(0, 1), 2) == false, "Partial depletion removed a deposit.");
        Check(SharedMapResourceHealth.Sections(Health(0), 2) == null, "An incomplete health array established depletion.");
        Check(SharedMapResourceHealth.Sections(Health(float.NaN, 0), 2) == null, "Invalid health established depletion.");
        Check(SharedMapResourceHealth.Sections("broken", 2) == null, "Malformed health established depletion.");
        var progress = SharedMapResourceHealth.Progress(Health(0, .1f, 100, -2), 4);
        Check(progress?.RemainingSections == 2 && progress?.TotalSections == 4, "Copper progress measured damage instead of remaining sections.");

        var nodes = new[] {
            new ResourceDeposit(1, ResourceKind.Tin, 0, 1, 0),
            new ResourceDeposit(2, ResourceKind.Tin, 20, 1, 0),
            new ResourceDeposit(3, ResourceKind.Tin, 40, 1, 0),
            new ResourceDeposit(4, ResourceKind.Copper, 0, 1, 0)
        };
        var grouped = SharedMapResourceClusters.Group(nodes, 30, true);
        Check(grouped.Count == 3 && grouped[0].Name == "Tin (2)" && grouped[1].Name == "Tin", "Grouping chained or mixed resource kinds.");
        var minedAnchor = nodes.Select(n => new ResourceDeposit(n.Identity, n.Kind, n.X, n.Y, n.Z, n.Identity == 1)).ToArray();
        var reduced = SharedMapResourceClusters.Group(minedAnchor, 30, true);
        Check(reduced.Count == 3 && reduced[0].Identity == "1" && reduced[0].Name == "Tin", "Depletion moved a Tin cluster's anchor or swallowed another cluster.");
        var exhausted = nodes.Select(n => new ResourceDeposit(n.Identity, n.Kind, n.X, n.Y, n.Z, true)).ToArray();
        Check(SharedMapResourceClusters.Group(exhausted, 30, true).Count == 0, "Exhausted groups remained published.");
        Check(SharedMapResourceClusters.Group(exhausted, 30, false).Count == 3, "Historical display lost groups.");
        Check(SharedMapResourceClusters.Group(nodes, 0, true).Count == 4, "Zero-distance grouping merged nodes.");

        var copper = new[] {
            new ResourceDeposit(1, ResourceKind.Copper, 0, 1, 0, progress: new MiningProgress(20, 100)),
            new ResourceDeposit(2, ResourceKind.Copper, 20, 1, 0, progress: new MiningProgress(10, 20))
        };
        Check(SharedMapResourceClusters.Group(copper, 30, true).Single().Name == "Copper 25%", "Copper percentages were not weighted by original sections.");
        var oneMined = new[] {
            new ResourceDeposit(1, ResourceKind.Copper, 0, 1, 0, true, new MiningProgress(0, 100)),
            new ResourceDeposit(2, ResourceKind.Copper, 20, 1, 0, progress: new MiningProgress(20, 20))
        };
        Check(SharedMapResourceClusters.Group(oneMined, 30, true).Single().Name == "Copper 17%", "Removing a boulder inflated the group's percentage.");
        Check(SharedMapResourceClusters.Group(new[] { new ResourceDeposit(1, ResourceKind.Copper, 0, 1, 0,
            progress: new MiningProgress(1, 8192)) }, 30, true).Single().Name == "Copper 1%", "A remaining Copper section displayed zero percent.");
        Check(SharedMapResourceClusters.Group(new[] { nodes[3] }, 30, true).Single().Name == "Copper ?%", "Unknown Copper progress was invented.");
        Check(SharedMapResourceClusters.Group(new[] { new ResourceDeposit(1, ResourceKind.Copper, 0, 1, 0, true) },
            30, false).Single().Name == "Copper 0%", "Historical depleted Copper did not display zero percent.");

        var silver = new[] {
            new ResourceDeposit(1, ResourceKind.Silver, 0, 1, 0, progress: new MiningProgress(8, 10)),
            new ResourceDeposit(2, ResourceKind.Silver, 5, 1, 0, progress: new MiningProgress(2, 10), silverDiscovered: true)
        };
        Check(SharedMapResourceClusters.Group(silver, 30, true).Single().Name == "Silver 20%",
            "Hidden Silver contributed to a published percentage.");
        Check(SharedMapResourceClusters.Group(new[] { silver[0] }, 30, false).Count == 0,
            "Historical marker display revealed locked Silver.");

        var map = new SharedMapExploration(64, 12, 100, 2);
        var resource = new[] { new MapMarker("Tin (2)", 0, 1, 0, "1") };
        byte[] original = PersonalPin(); var personal = Pins(original).Single();
        byte[] merged = SharedMapTableData.Merge(map, original, out _, Array.Empty<MapMarker>(), resources: resource)!;
        var pins = Pins(merged);
        Check(pins.Single(p => p.Owner == 42).Raw.SequenceEqual(personal.Raw), "Resource sync changed a nearby personal dot.");
        Check(pins.Single(p => p.Owner == SharedMapTableData.ResourceOwner).Type == 3, "Resources did not use the dot/service owner.");
        Check(SharedMapTableData.Merge(map, merged, out _, Array.Empty<MapMarker>(), resources: resource) == null, "Unchanged resources rewrote a table.");
        var portal = new[] { new MapMarker("Tin (2)", 0, 1, 0, "portal") };
        // Isolate label reconciliation from the independently configurable proximity rule.
        byte[] both = SharedMapTableData.Merge(map, merged, out _, portal, true, resources: resource, resourceMarkerHideDistanceMeters: 0)!;
        Check(Pins(both).Any(p => p.Owner == SharedMapTableData.ResourceOwner) && Pins(both).Any(p => p.Owner == SharedMapTableData.PortalOwner), "Matching portal/resource labels removed a managed source.");
        byte[] disabled = SharedMapTableData.Merge(map, merged, out _, Array.Empty<MapMarker>(), resources: Array.Empty<MapMarker>())!;
        Check(Pins(disabled).Count == 1 && Pins(disabled)[0].Raw.SequenceEqual(personal.Raw), "Disabling resources removed a personal pin or retained generated pins.");
        System.Console.WriteLine("SharedMap resource health, grouping and pin-preservation checks passed.");
    }
}
