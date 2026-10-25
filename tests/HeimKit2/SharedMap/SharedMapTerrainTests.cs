using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.IO.Compression;
using HeimKit.Features.SharedMap;

internal static class SharedMapTerrainTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static byte[] Terrain(int operations, float delta, bool trailing = false)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
        {
            writer.Write(1); writer.Write(operations);
            writer.Write(1f); writer.Write(2f); writer.Write(3f); writer.Write(2f);
            writer.Write(1); writer.Write(true); writer.Write(delta); writer.Write(0f);
            writer.Write(1); writer.Write(false);
            if (trailing) writer.Write((byte)1);
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(data.ToArray());
        return output.ToArray();
    }
    public static void Run()
    {
        var before = SharedMapTerrainData.Read(Terrain(1, 0));
        var after = SharedMapTerrainData.Read(Terrain(2, -1));
        Check(before != null && after?.X == 1 && after.Y == 2 && after.Z == 3 &&
            SharedMapTerrainData.Lowered(before, after), "Valid native terrain lowering was lost.");
        Check(!SharedMapTerrainData.Lowered(null, after), "Initial historical replication counted as a new dig.");
        Check(!SharedMapTerrainData.Lowered(before, SharedMapTerrainData.Read(Terrain(1, -1))), "An unchanged revision counted as a new dig.");
        Check(!SharedMapTerrainData.Lowered(before, SharedMapTerrainData.Read(Terrain(2, 1))), "Terrain raising counted as digging.");
        Check(!SharedMapTerrainData.Lowered(before, SharedMapTerrainData.Read(Terrain(2, 0))), "Painting/no height change counted as digging.");
        Check(!SharedMapTerrainData.Lowered(before, SharedMapTerrainData.Read(Terrain(100, -1))), "A large historical gap counted as one recent dig.");
        Check(SharedMapTerrainData.Read(Terrain(2, float.NaN)) == null &&
            SharedMapTerrainData.Read(Terrain(2, -1, true)) == null &&
            SharedMapTerrainData.Read(new byte[] { 1, 2, 3 }) == null, "Malformed terrain supplied discovery evidence.");
        System.Console.WriteLine("SharedMap native terrain evidence checks passed.");
    }
}
