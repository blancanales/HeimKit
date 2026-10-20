using System.IO.Compression;
using System.Text;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapDungeonTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static MapMarker Marker(string path, float x, float z) => new("Burial Chamber", x, 1, z,
        SharedMapDungeonIdentity.Create("Crypt2", 10, 1, 10, path));

    private static byte[] PlayerHammer()
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(4096); writer.Write(new byte[4096]); writer.Write(1);
            writer.Write(42L); writer.Write("My entrance note"); writer.Write(10f); writer.Write(1f); writer.Write(10f);
            writer.Write(2); writer.Write(true); writer.Write("Steam_42");
        }
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(raw.ToArray());
        return output.ToArray();
    }

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-dungeons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Marker("1/0", 10, 10);
            var second = Marker("1/1", 10.1f, 10);
            Check(first.Identity != second.Identity, "Distinct gateway paths acquired one discovery identity.");
            Check(first.Identity != SharedMapDungeonIdentity.Create("Crypt2", 10.1f, 1, 10, "1/0"), "Distinct nearby roots were merged.");
            Check(SharedMapDungeonIdentity.Create("Crypt2", 0, 1, 0, "1") == SharedMapDungeonIdentity.Create("Crypt2", -0f, 1, -0f, "1"),
                "Signed zero changed a dungeon identity.");
            var store = new SharedMapDungeonStore(directory, 123);
            Check(!store.Load() && !store.Save(), "An absent empty store wrote an unnecessary file.");
            Check(!store.DiscoverNearby(first, -40.01f, 10, 50), "Discovery used the nearby location root rather than the actual entrance radius.");
            Check(!store.DiscoverNearby(first, float.NaN, 10, 50), "Unavailable player coordinates discovered an entrance.");
            Check(store.DiscoverNearby(first, -40, 10, 50), "An entrance on the exploration boundary was missed.");
            Check(!store.Discover(first) && store.Discover(second) && store.Count == 2, "Discoveries were duplicated or nearby entrances were clustered.");
            Check(store.Save() && !store.Save(), "Dungeon persistence failed or rewrote unchanged state.");
            var loaded = new SharedMapDungeonStore(directory, 123);
            Check(loaded.Load() && loaded.Count == 2 && loaded.Read().Select(p => p.Identity).Distinct().Count() == 2,
                "Restart lost distinct entrances or their stable identities.");
            var map = new SharedMapExploration(64, 12, 50, .25f);
            var historical = Marker("2", 250, 250);
            Check(!loaded.DiscoverExplored(historical, map), "Retrospective discovery revealed unexplored terrain.");
            map.Reveal(250, 250);
            Check(loaded.DiscoverExplored(historical, map), "Explored entrance did not support retrospective discovery.");
            Check(loaded.Save() && File.Exists(loaded.FilePath + ".bak"), "Dungeon saves did not retain the previous destination.");
            try { new SharedMapDungeonStore(directory, 456).Discover(new MapMarker("Invalid", 0, 5000, 0, first.Identity)); throw new Exception("Interior point accepted."); }
            catch (InvalidDataException) { }

            byte[] player = PlayerHammer();
            var originalPin = SharedMapTableData.ReadPins(64, player).Single();
            var sameLabelPortal = new[] { new MapMarker("Burial Chamber", 10, 1, 10) };
            byte[] merged = SharedMapTableData.Merge(map, player, out _, portals: sameLabelPortal, managePortalIcons: true,
                dungeons: loaded.Read())!;
            var pins = SharedMapTableData.ReadPins(64, merged);
            var preserved = pins.Single(p => p.Owner == 42);
            Check(preserved.Icon == 2 && preserved.Checked && preserved.Author == originalPin.Author && preserved.Name == originalPin.Name &&
                preserved.X == originalPin.X && preserved.Y == originalPin.Y && preserved.Z == originalPin.Z,
                "Dungeon/portal reconciliation changed a nearby player-owned hammer pin.");
            Check(pins.Count(p => p.Owner == SharedMapTableData.DungeonOwner) == 2 &&
                pins.Where(p => p.Owner == SharedMapTableData.DungeonOwner).All(p => p.Icon == 2), "Dungeon publication failed the ten-metre rule or lost the native hammer icon.");
            Check(SharedMapTableData.Merge(map, merged, out _, portals: sameLabelPortal, managePortalIcons: true, dungeons: loaded.Read()) == null,
                "Stable dungeon markers rewrote table data.");
            var disabled = SharedMapTableData.Merge(map, merged, out _, dungeons: Array.Empty<MapMarker>())!;
            Check(SharedMapTableData.ReadPins(64, disabled).Single().Owner == 42, "Disabling dungeon markers deleted a personal hammer pin.");
            var anchor = new SharedMapDungeonPins(loaded.Read()).Markers.Single(m => m.X < 100);
            string removedId = SharedMapPinRemovals.Identity(new MapPin(SharedMapTableData.DungeonOwner, anchor.Name, anchor.X, anchor.Y, anchor.Z, 2));
            byte[] removed = SharedMapTableData.Merge(map, player, out _, dungeons: loaded.Read(),
                removePin: p => SharedMapPinRemovals.Identity(p) == removedId)!;
            Check(SharedMapTableData.ReadPins(64, removed).Count(p => p.Owner == SharedMapTableData.DungeonOwner) == 1,
                "Selected pin removal exposed a suppressed nearby source instead of retaining the exclusion.");
            var tooMany = Enumerable.Range(0, 4097).Select(i => Marker(i.ToString(), i, 0)).ToArray();
            try { SharedMapTableData.Merge(map, player, out _, dungeons: tooMany); throw new Exception("Marker overflow was published."); }
            catch (InvalidDataException) { }

            var otherWorld = new SharedMapDungeonStore(directory, 456);
            File.Copy(loaded.FilePath, otherWorld.FilePath);
            try { otherWorld.Load(); throw new Exception("Wrong-world discoveries accepted."); }
            catch (InvalidDataException) { }
            byte[] corrupt = File.ReadAllBytes(loaded.FilePath); corrupt[^1] ^= 1; File.WriteAllBytes(loaded.FilePath, corrupt);
            try { new SharedMapDungeonStore(directory, 123).Load(); throw new Exception("Corrupt discoveries accepted."); }
            catch (InvalidDataException) { }
            Check(File.ReadAllBytes(loaded.FilePath).SequenceEqual(corrupt), "Loading overwrote corrupt dungeon discoveries.");
            File.Delete(loaded.FilePath);
            try { new SharedMapDungeonStore(directory, 123).Load(); throw new Exception("Orphaned backup silently reset discoveries."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: dungeon identity, encounter radius, retrospective eligibility, persistence, managed ownership and personal hammer preservation.");
    }
}
