using System.IO.Compression;
using System.Reflection;
using System.Text;
using HeimKit.Core.Native;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapBlackMarbleTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run() { CatalogAndMiningHandoff(); PersistenceAndPublication(); }
    private static string Health(params float[] sections)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(sections.Length); foreach (float value in sections) writer.Write(value);
        return Convert.ToBase64String(stream.ToArray());
    }
    private static void CatalogAndMiningHandoff()
    {
        Check((int)ResourceKind.Copper == 0 && (int)ResourceKind.Tin == 1 && (int)ResourceKind.Silver == 2 &&
            (int)ResourceKind.Obsidian == 3 && (int)ResourceKind.Flametal == 4 && (int)ResourceKind.BlackMarble == 5,
            "The appended resource kind renumbered existing persisted discoveries.");
        var catalog = ((ValueTuple<string, ResourceKind, string, string?>[])typeof(SharedMapResources)
            .GetField("Catalog", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .Where(e => e.Item2 == ResourceKind.BlackMarble).ToDictionary(e => e.Item1);
        Check(catalog.Count == 4 && catalog.Keys.OrderBy(n => n).SequenceEqual(new[]
            { "giant_ribs", "giant_ribs_frac", "giant_skull", "giant_skull_frac" }) &&
            catalog.Values.All(e => e.Item3 == "BlackMarble") &&
            catalog["giant_ribs"].Item4 == "giant_ribs_frac" && catalog["giant_skull"].Item4 == "giant_skull_frac" &&
            catalog["giant_ribs_frac"].Item4 == null && catalog["giant_skull_frac"].Item4 == null,
            "Black Marble escaped its native resource-only allowlist or lost a validated mining successor.");
        var constructor = typeof(SharedMapResourcePrefab).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        using var world = new ReviewRegressionTests.World(); var record = world.Add(32001);
        var shell = (SharedMapResourcePrefab)constructor.Invoke(new object[] { "giant_ribs", ResourceKind.BlackMarble, true, 4, false, false, 1f, 0f });
        var fractured = (SharedMapResourcePrefab)constructor.Invoke(new object[] { "giant_ribs_frac", ResourceKind.BlackMarble, false, 4, false, true, 100f, 0f });
        Check(shell.Progress(record)?.RemainingSections == 4 && shell.Depleted(record) == false,
            "An untouched shell lost its successor's complete mining section count.");
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_health, 0f);
        Check(shell.ReplacesWithMining && shell.Progress(record) == null && shell.Depleted(record) == null,
            "Destroying the intact shell was mistaken for exhausted Black Marble.");
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_health, Health(100, 0, 25, -1));
        Check(fractured.Progress(record)?.RemainingSections == 2 && fractured.Depleted(record) == false,
            "Partially mined Black Marble disappeared or used remaining hit points as section progress.");
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_health, "malformed");
        Check(fractured.Progress(record) == null && fractured.Depleted(record) == null,
            "Unobserved/malformed mining state established depletion.");
        ZDOExtraData.Set(record.m_uid, ZDOVars.s_health, Health(0, 0, 0, 0));
        Check(!fractured.ReplacesWithMining && fractured.Depleted(record) == true,
            "Fully mined fractured remains did not reach terminal depletion.");
        System.Console.WriteLine("PASS: Black Marble uses only verified rib/skull families, preserves persisted enum IDs, and distinguishes intact-shell handoff, partial mining, unknown health and terminal depletion.");
    }
    private static byte[] PersonalMap()
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(64 * 64); writer.Write(new byte[64 * 64]); writer.Write(1);
            writer.Write(42L); writer.Write("Black Marble"); writer.Write(0f); writer.Write(1f); writer.Write(0f);
            writer.Write(3); writer.Write(true); writer.Write("personal");
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw.ToArray());
        return output.ToArray();
    }
    private static void PersistenceAndPublication()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(root, "heimkit-black-marble-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SharedMapResourceStore(directory, 4321);
            foreach (var kind in new[] { ResourceKind.Copper, ResourceKind.Tin, ResourceKind.Silver, ResourceKind.Obsidian, ResourceKind.Flametal })
                Check(store.Observe(kind, 100 + (int)kind * 100, 1, 0, false, true, silverDiscovered: kind == ResourceKind.Silver) != null,
                    "A legacy discovery no longer loads under its existing resource kind.");
            store.Save(); var upgraded = new SharedMapResourceStore(directory, 4321); upgraded.Load();
            Check(upgraded.Deposits.Count == 5 && upgraded.Deposits.Select(d => (int)d.Kind).SequenceEqual(Enumerable.Range(0, 5)),
                "Adding the new kind changed an existing saved resource file.");
            var first = upgraded.Observe(ResourceKind.BlackMarble, 0, 1, 0, false, true, new MiningProgress(4, 4))!;
            var successor = upgraded.Observe(ResourceKind.BlackMarble, 0, 1, 0, false, true, new MiningProgress(2, 4))!;
            Check(first.Identity == successor.Identity && upgraded.Deposits.Count == 6, "An intact-to-fractured handoff duplicated a discovery.");
            upgraded.Save(); var reloaded = new SharedMapResourceStore(directory, 4321); reloaded.Load();
            Check(reloaded.Deposits.Last().Kind == ResourceKind.BlackMarble && reloaded.Deposits.Last().Progress?.RemainingSections == 2,
                "Restart lost Black Marble or its accepted mining progress.");
            reloaded.Observe(ResourceKind.BlackMarble, 0, 1, 0, null, false);
            var markers = SharedMapResourceClusters.Group(reloaded.Deposits, 0, true);
            Check(markers.Any(m => m.Name == "Black Marble 50%"), "The new resource did not publish its readable percentage label.");
            var map = new SharedMapExploration(64, 12, 50, .3f);
            byte[] merged = SharedMapTableData.Merge(map, PersonalMap(), out _, resources: markers)!;
            var pins = SharedMapTableData.ReadPins(64, merged);
            Check(pins.Any(p => p.Owner == SharedMapTableData.ResourceOwner && p.Name == "Black Marble 50%" && p.Icon == 3) &&
                pins.Any(p => p.Owner == 42 && p.Name == "Black Marble" && p.Checked && p.Author == "personal"),
                "Black Marble marker publication changed a personal pin or the resource marker owner/icon contract.");
            reloaded.Observe(ResourceKind.BlackMarble, 0, 1, 0, true, false, new MiningProgress(0, 4));
            Check(!SharedMapResourceClusters.Group(reloaded.Deposits, 0, true).Any(m => m.Name.StartsWith("Black Marble")) &&
                SharedMapResourceClusters.Group(reloaded.Deposits, 0, false).Any(m => m.Name == "Black Marble 0%"),
                "Black Marble ignored depleted-removal or historical-display settings.");
            byte[] cleaned = SharedMapTableData.Merge(map, merged, out _, resources: SharedMapResourceClusters.Group(reloaded.Deposits, 0, true))!;
            Check(SharedMapTableData.ReadPins(64, cleaned).Where(p => p.Name.StartsWith("Black Marble")).All(p => p.Owner == 42),
                "A depleted generated marker remained or cleanup removed the player's personal copy.");
            System.Console.WriteLine("PASS: legacy discovery files remain readable, Black Marble progress survives restart/handoff, native dot publication preserves personal pins, and depletion honors active/historical display.");
        }
        finally
        {
            string relative = Path.GetRelativePath(root, Path.GetFullPath(directory));
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) &&
                Path.GetFileName(directory).StartsWith("heimkit-black-marble-", StringComparison.Ordinal) && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
