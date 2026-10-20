using System.IO.Compression;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapLandmarkTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static byte[] Table(SharedMapExploration map, params MapPin[] pins)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(map.Size * map.Size); writer.Write(new byte[map.Size * map.Size]); writer.Write(pins.Length);
            foreach (var p in pins)
            { writer.Write(p.Owner); writer.Write(p.Name); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Icon); writer.Write(p.Checked); writer.Write(p.Author); }
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) raw.WriteTo(gzip);
        return output.ToArray();
    }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-landmarks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var settings = new SharedMapSettings(new ConfigFile(Path.Combine(directory, "heimkit.cfg"), false) { SaveOnConfigSet = false });
            Check(settings.RunestoneMarkerSync.Value && settings.SpawnerMarkerSync.Value, "Landmark sources must default enabled.");
            settings.RunestoneMarkerSync.Value = false;
            Check(settings.SpawnerMarkerSync.Value && settings.PortalMarkerSync.Value && settings.ResourceMarkerSync.Value && settings.DungeonMarkerSync.Value, "Runestones changed another source switch.");
            var type = typeof(SharedMap).Assembly.GetType("HeimKit.Features.SharedMap.SharedMapLandmarkPrefab", true)!;
            var repeating = type.GetMethod("Repeating", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (float interval in new[] { 0, -1, float.NaN, float.PositiveInfinity })
                Check(!(bool)repeating.Invoke(null, new object[] { interval })!, "Invalid/one-shot source was classified as recurring.");
            Check((bool)repeating.Invoke(null, new object[] { 60f })!, "Effective positive recurrence was rejected.");
            var physicalLabels = type.GetMethod("PhysicalSpawnerLabel", BindingFlags.NonPublic | BindingFlags.Static)!;
            var physicalPrefabs = type.GetMethod("PhysicalSpawnerPrefab", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (string name in new[] { "Spawner_GreydwarfNest", "BonePileSpawner", "BonePileSpawner_swamp", "Spawner_DraugrPile" })
                Check((bool)physicalPrefabs.Invoke(null, new object[] { name })!, "Physical source missing from audited catalog: " + name);
            foreach (string name in new[] { "Spawner_Skeleton_respawn_30", "Spawner_Surtling", "Spawner_Greydwarf", "Spawner_GreydwarfNest_custom", "EvilHeart_Swamp" })
                Check(!(bool)physicalPrefabs.Invoke(null, new object[] { name })!, "Hidden or unsupported source admitted: " + name);
            Check((bool)physicalLabels.Invoke(null, new object[] { "Greydwarf Nest" })! &&
                (bool)physicalLabels.Invoke(null, new object[] { "Skeleton Bone Pile" })! &&
                !(bool)physicalLabels.Invoke(null, new object[] { "Skeleton Spawner" })! &&
                !(bool)physicalLabels.Invoke(null, new object[] { "Greydwarf Spawner" })!, "Physical nests/piles and hidden spawn instructions were conflated.");
            var labels = (IReadOnlyDictionary<string, string>)type.GetField("Runestones", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Check(labels.Count == 11 && labels["Runestone_Boars"] == "Boar Runestone" && !labels.ContainsKey("BossStone_Eikthyr") && !labels.ContainsKey("Vegvisir"), "Lore-stone scope changed.");
            var map = new SharedMapExploration(64, 12, 50, .25f);
            Check(SharedMapLandmarkStore.SourceIdentity("BlackIce_Core", 1, 10, 2, "root", true) ==
                SharedMapLandmarkStore.SourceIdentity("BlackIce_Core", 1, 2, 2, "root", true), "Vertical native settling changed a persistent spawner identity.");
            Check(SharedMapLandmarkStore.SourceIdentity("Spawner_Hole_double", 1, 2, 3, "0/0", true) !=
                SharedMapLandmarkStore.SourceIdentity("Spawner_Hole_double", 1, 2, 3, "0/1", true), "Distinct child source paths merged.");
            var runes = new SharedMapLandmarkStore(directory, 123, false); var sources = new SharedMapLandmarkStore(directory, 123, true);
            string identity = SharedMapDungeonIdentity.Create("Source", 100, 1, 100, "root");
            string sibling = SharedMapDungeonIdentity.Create("Source", 101, 1, 100, "root");
            var marker = new MapMarker("Greydwarf Nest", 100, 1, 100, identity);
            Check(!sources.Discover(marker, 0, 0, 50, map, false), "Location/biome proximity replaced actual-point proximity.");
            Check(!sources.Discover(marker, null, null, 50, map, true), "Retrospective mode revealed unexplored terrain.");
            map.Reveal(100, 100);
            Check(!sources.Discover(marker, null, null, 50, map, false) && sources.Discover(marker, null, null, 50, map, true), "Retrospective eligibility ignored its switch.");
            Check(sources.Discover(new MapMarker("Greydwarf Nest", 101, 1, 100, sibling), 100, 100, 50, map, false), "Distinct nearby same-label sources merged.");
            Check(sources.Read(true).Count == 2 && sources.Read(false).Count == 0 && sources.Count == 2, "Disablement erased discovery history.");
            sources.Save(); var loaded = new SharedMapLandmarkStore(directory, 123, true); loaded.Load();
            Check(loaded.Read(true).Count == 2 && !loaded.Retired(identity), "Restart or unload fabricated destruction.");
            Check(loaded.Retire(identity) && !loaded.Retire(identity) && !loaded.Retire(new string('f', 64)), "Terminal retirement affected an unrelated or absent source.");
            Check(loaded.Read(true).Single().Identity == sibling && loaded.Read(true, false).Count == 2, "Historical retention changed another source.");
            loaded.Save(); loaded = new SharedMapLandmarkStore(directory, 123, true); loaded.Load();
            Check(!loaded.Discover(marker, 100, 100, 50, map, false) && loaded.Retired(identity) && loaded.Read(true).Count == 1, "Reassociation resurrected a terminal tombstone.");
            try { loaded.Discover(new MapMarker(marker.Name, 102, 1, 100, identity), 100, 100, 50, map, false); throw new Exception("Conflicting source reassociation accepted."); } catch (InvalidDataException) { }
            var legacy = new SharedMapLandmarkStore(directory, 125, true);
            foreach (string label in new[] { "Skeleton Spawner", "Surtling Spawner", "Growth Spawner", "Seeker Spawner", "Tetra Spawner", "Kvastur Spawner" })
            {
                string hiddenId = SharedMapDungeonIdentity.Create(label, 100, 1, 100, "root");
                legacy.Discover(new MapMarker(label, 100, 1, 100, hiddenId), 100, 100, 50, map, false);
            }
            legacy.Discover(marker, 100, 100, 50, map, false);
            Check(legacy.Count == 7 && legacy.Read(true).Single().Name == "Greydwarf Nest" && legacy.Read(true, false).Count == 1,
                "Legacy hidden-point discoveries remained published or erased physical history.");
            legacy.Save(); legacy = new SharedMapLandmarkStore(directory, 125, true); legacy.Load();
            Check(legacy.Count == 7 && legacy.Read(true).Single().Identity == identity && !legacy.Retired(identity),
                "Restart lost legacy policy filtering or fabricated destruction.");
            var staleHidden = new MapPin(SharedMapTableData.SpawnerOwner, "Skeleton Spawner", 100, 1, 100, 3);
            byte[] staleTable = Table(map, staleHidden, new MapPin(77, "My note", 101, 1, 100, 3));
            var reconciled = SharedMapTableData.Merge(map, staleTable, out _, spawners: legacy.Read(true))!;
            var remainingPins = SharedMapTableData.ReadPins(64, reconciled);
            Check(!remainingPins.Any(p => p.Name == "Skeleton Spawner") && remainingPins.Any(p => p.Name == "Greydwarf Nest") &&
                remainingPins.Any(p => p.Owner == 77 && p.Name == "My note"), "Legacy shared hidden marker survived reconciliation or unrelated pins were removed.");
            var stone = new MapMarker("Boar Runestone", 100, 1, 100, identity);
            runes.Discover(stone, 100, 100, 50, map, false);
            Check(!runes.Retire(identity) && runes.Read(true).Count == 1, "A static stone acquired invented destruction state.");
            var playerDot = new MapPin(77, marker.Name, 100, 1, 100, 3, false, "Steam_77");
            var playerHammer = new MapPin(78, "Home", 101, 1, 100, 2);
            var stale = new MapPin(SharedMapTableData.SpawnerOwner, marker.Name, 100, 1, 100, 3, true, "Steam_admin_spoof");
            var resource = new[] { new MapMarker("Copper", 130, 1, 130, sibling) };
            var table = SharedMapTableData.Merge(map, Table(map, playerDot, playerHammer, stale), out _, resources: resource,
                runestones: runes.Read(true), spawners: loaded.Read(true))!;
            var pins = SharedMapTableData.ReadPins(64, table);
            Check(pins.Any(p => p.Owner == 77) && pins.Any(p => p.Owner == 78) && pins.Count(p => p.Owner == SharedMapTableData.ResourceOwner) == 1,
                "Landmark normalization removed unrelated dots, hammers or resources.");
            Check(pins.Count(p => p.Owner == SharedMapTableData.SpawnerOwner) == 1 && pins.Any(p => p.Owner == SharedMapTableData.RunestoneOwner && p.Icon == 3),
                "Stale upload restored a retired source or markers lost reserved dot identities.");
            var off = SharedMapTableData.Merge(map, table, out _, resources: resource, runestones: Array.Empty<MapMarker>(), spawners: loaded.Read(true))!;
            Check(SharedMapTableData.ReadPins(64, off).All(p => p.Owner != SharedMapTableData.RunestoneOwner) && loaded.Count == 2 && runes.Count == 1, "Independent disablement damaged history or another source.");
            var state = new SharedMapPinState(directory, 123);
            var issued = state.Publish(new MapPin(SharedMapTableData.SpawnerOwner, marker.Name, 100, 1, 100, 3), identity, false);
            var adopted = new MapPin(99, issued.Name, issued.X, issued.Y, issued.Z, issued.Icon, true, "Steam_admin_spoof");
            Check(state.Accept(new[] { adopted }, new[] { issued }, false) == 0 && state.Adopted(adopted) && loaded.Retired(identity),
                "Checkmark/adoption without sharing mutated destruction state or lost source matching.");
            Check(!state.Publish(issued, identity, false).Checked, "Spoofed author fields authorized shared X state while disabled.");
            var removals = new SharedMapPinRemovals(directory, 123); removals.Set(SharedMapPinRemovals.Identity(pins.Single(p => p.Owner == SharedMapTableData.RunestoneOwner)), true);
            var removed = SharedMapTableData.Merge(map, table, out _, resources: resource, runestones: runes.Read(true), spawners: loaded.Read(true), removePin: removals.Contains)!;
            Check(SharedMapTableData.ReadPins(64, removed).All(p => p.Owner != SharedMapTableData.RunestoneOwner), "Selected removal did not apply to runestones.");
            removals.Set(SharedMapPinRemovals.Identity(pins.Single(p => p.Owner == SharedMapTableData.RunestoneOwner)), false);
            Check(SharedMapTableData.ReadPins(64, SharedMapTableData.Merge(map, removed, out _, runestones: runes.Read(true), removePin: removals.Contains)!).Any(p => p.Owner == SharedMapTableData.RunestoneOwner), "Allowed marker failed to republish.");
            Check(File.Exists(loaded.FilePath + ".bak"), "Atomic landmark replacement omitted its backup.");
            Check(!new SharedMapLandmarkStore(directory, 124, true).Load(), "An absent different world did not start empty.");
            var wrongWorld = new SharedMapLandmarkStore(directory, 124, true);
            File.Copy(loaded.FilePath, wrongWorld.FilePath);
            try { wrongWorld.Load(); throw new Exception("Landmark state from another world accepted."); } catch (InvalidDataException) { }
            var wrongKind = new SharedMapLandmarkStore(directory, 123, false);
            File.Copy(loaded.FilePath, wrongKind.FilePath);
            try { wrongKind.Load(); throw new Exception("Wrong landmark source kind accepted."); } catch (InvalidDataException) { }
            var overflow = Enumerable.Repeat(stone, SharedMapLandmarkStore.MaximumMarkers + 1).ToArray();
            byte[] priorValid = table.ToArray();
            try { SharedMapTableData.Merge(map, table, out _, runestones: overflow); throw new Exception("Oversized marker catalog accepted."); } catch (InvalidDataException) { }
            Check(table.SequenceEqual(priorValid), "Overflow modified prior valid table bytes.");
            byte[] corrupt = File.ReadAllBytes(loaded.FilePath); corrupt[^1] ^= 1; File.WriteAllBytes(loaded.FilePath, corrupt);
            try { new SharedMapLandmarkStore(directory, 123, true).Load(); throw new Exception("Corrupt landmark state accepted."); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: landmark scope/defaults, actual-point discovery, independent switches, restart/tombstones, stale uploads, adoption and unrelated-pin preservation.");
    }
}
