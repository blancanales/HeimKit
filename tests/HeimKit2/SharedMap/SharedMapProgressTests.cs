using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;
using System.Security.Cryptography;
using System.Text;

internal static class SharedMapProgressTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static MapPin Toggle(MapPin p, bool check) => new(p.Owner, p.Name, p.X, p.Y, p.Z, p.Icon, check, p.Author);
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-progress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            // Preserve exact-site deduplication and ambiguity rejection across cell boundaries.
            var findSite = typeof(SharedMap).Assembly.GetType("HeimKit.Features.SharedMap.SharedMapDungeonLoot", true)!
                .GetMethod("FindSite", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var sites = new Dictionary<(int Hash, int X, int Y, int Z), List<UnityEngine.Vector3>>();
            sites[(7, 0, 0, 0)] = new() { new(.99f, 0, 0), new(.99f, 0, 0) };
            int Match(int hash, float x) => (int)findSite.Invoke(null, new object?[] { sites, hash, new UnityEngine.Vector3(x, 0, 0), null })!;
            Check(Match(7, 1.01f) == 1, "Repeated identical sites or neighboring cells lost a unique pickup match.");
            Check(Match(8, 1.01f) == 0 && Match(7, 2f) == 0, "An unrelated prefab or distant point matched a pickup site.");
            sites[(7, 1, 0, 0)] = new() { new(1.02f, 0, 0) };
            Check(Match(7, 1.01f) == 2, "Two distinct nearby sites were accepted as a unique pickup match.");
            Check(SharedMapDungeonStore.ProgressLabel("Burial Chamber", 5, 3) == "Burial Chamber 40%", "Wrong remaining percentage.");
            Check(SharedMapDungeonStore.ProgressLabel("Frost Cave", 3, 3) == "Frost Cave 0%", "Fully picked inventory did not show zero.");
            Check(SharedMapDungeonStore.ProgressLabel("Cave", 0, 0) == "Cave 0%" && SharedMapDungeonStore.ProgressLabel("Cave", 5, -1) == "Cave", "Empty and unknown inventories were conflated.");
            Check(SharedMapDungeonStore.ProgressLabel("Cave", 101, 100) == "Cave 1%", "Positive remainder rounded to zero.");
            string id = SharedMapDungeonIdentity.Create("Crypt2", 1, 2, 3, "1/0");
            var store = new SharedMapDungeonStore(directory, 45); store.Discover(new MapMarker("Burial Chamber", 1, 2, 3, id));
            Check(store.Progress(id, 5, 3) && store.Save(), "Progress was not persisted.");
            var reloaded = new SharedMapDungeonStore(directory, 45); reloaded.Load();
            Check(!reloaded.Visited(id) && reloaded.Read().Single().Name == "Burial Chamber", "An unvisited dungeon disclosed its saved percentage.");
            Check(reloaded.Visit(id) && !reloaded.Visit(id) && !reloaded.Visit(new string('a', 64)), "Visits must be idempotent and require an existing discovery.");
            reloaded.Save(); reloaded = new SharedMapDungeonStore(directory, 45); reloaded.Load();
            Check(reloaded.Read().Single().Name == "Burial Chamber 40%" && reloaded.Read(false).Single().Name == "Burial Chamber", "Restart/option disablement changed the baseline label.");
            reloaded.Progress(id, 2, 2); Check(reloaded.Read().Single().Name == "Burial Chamber", "Missing pickup records falsely completed the dungeon.");
            reloaded.Progress(id, 5, 5); Check(reloaded.Read().Single().Name == "Burial Chamber 0%", "Complete picked state did not recover from unknown.");
            reloaded.Progress(id, 0, 0, verifiedEmpty: true);
            Check(reloaded.Read().Single().Name == "Burial Chamber", "Missing established pickups falsely became an empty dungeon.");
            string emptyId = SharedMapDungeonIdentity.Create("Crypt2", 10, 2, 3, "1/0");
            reloaded.Discover(new MapMarker("Burial Chamber", 10, 2, 3, emptyId));
            reloaded.Progress(emptyId, 0, 0, verifiedEmpty: true);
            Check(reloaded.Read().Single(m => m.Identity == emptyId).Name == "Burial Chamber", "An empty unvisited dungeon disclosed its inventory.");
            reloaded.Visit(emptyId); reloaded.Save();
            var emptyRestart = new SharedMapDungeonStore(directory, 45); emptyRestart.Load();
            Check(emptyRestart.Visited(emptyId) && emptyRestart.Read().Single(m => m.Identity == emptyId).Name == "Burial Chamber 0%", "Visited empty state did not survive restart.");
            string siblingId = SharedMapDungeonIdentity.Create("Crypt2", 1, 2, 3, "1/2");
            emptyRestart.Discover(new MapMarker("Burial Chamber", 4, 2, 3, siblingId));
            Check(emptyRestart.ShareVisits(new[] { id, siblingId }) == 1 && emptyRestart.Visited(siblingId), "A later-discovered gateway lost its sibling's visit.");
            Check(emptyRestart.Read().Count == 3 && emptyRestart.ShareVisits(new[] { id, siblingId }) == 0, "Visit sharing merged independent gateway publications or repeated visits.");
            var inferred = new SharedMapDungeonStore(directory, 49);
            inferred.Discover(new MapMarker("Burial Chamber", 1, 2, 3, id));
            inferred.Progress(id, 5, 0);
            Check(!inferred.VisitFromPickups(id, 5, 0) && inferred.Read().Single().Name == "Burial Chamber", "Untouched pickups disclosed the dungeon inventory.");
            Check(!inferred.VisitFromPickups(id, 5, 1), "Unaccepted pickup counts inferred a visit.");
            inferred.Progress(id, 5, 1);
            Check(inferred.VisitFromPickups(id, 5, 1) && inferred.Read().Single().Name == "Burial Chamber 80%", "Verified picked items failed to establish a prior visit.");
            inferred.Save(); var inferredRestart = new SharedMapDungeonStore(directory, 49); inferredRestart.Load();
            Check(inferredRestart.Visited(id) && inferredRestart.Read().Single().Name == "Burial Chamber 80%", "Inferred visits were lost on restart.");
            inferredRestart.Progress(id, 2, 1);
            Check(!inferredRestart.VisitFromPickups(id, 2, 1) && inferredRestart.Read().Single().Name == "Burial Chamber", "Incomplete pickup evidence was accepted as a reliable inventory.");
            var emptyUnvisited = new SharedMapDungeonStore(directory, 50);
            emptyUnvisited.Discover(new MapMarker("Burial Chamber", 1, 2, 3, id)); emptyUnvisited.Progress(id, 0, 0, verifiedEmpty: true);
            Check(!emptyUnvisited.VisitFromPickups(id, 0, 0) && emptyUnvisited.Read().Single().Name == "Burial Chamber", "An empty inventory fabricated a prior visit.");
            // Older files never prove a visit, even when they already contain pickup counts.
            foreach (var old in new[] { (Format: 1, Total: 0, Picked: 0), (Format: 2, Total: 0, Picked: 0), (Format: 2, Total: 5, Picked: 3) })
            {
                using var body = new MemoryStream();
                using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
                {
                    writer.Write(0x47444B48u); writer.Write(old.Format); writer.Write(46L); writer.Write(1);
                    writer.Write(Convert.FromHexString(id)); byte[] name = Encoding.UTF8.GetBytes("Burial Chamber");
                    writer.Write((ushort)name.Length); writer.Write(name); writer.Write(1f); writer.Write(2f); writer.Write(3f);
                    if (old.Format == 2) { writer.Write(old.Total); writer.Write(old.Picked); }
                }
                var legacy = new SharedMapDungeonStore(directory, 46); byte[] bytes = body.ToArray();
                File.WriteAllBytes(legacy.FilePath, bytes.Concat(SHA256.HashData(bytes)).ToArray());
                legacy.Load(); Check(!legacy.Visited(id) && legacy.Read().Single().Name == "Burial Chamber", "Legacy discovery fabricated a visit or disclosed progress.");
                legacy.Visit(id);
                Check(legacy.Read().Single().Name == (old.Total > 0 ? "Burial Chamber 40%" : "Burial Chamber"), "Legacy zero-total progress was mistaken for verified empty.");
            }

            var state = new SharedMapPinState(directory, 45);
            var marker = new MapPin(SharedMapTableData.DungeonOwner, "Burial Chamber 40%", 100, 1, 100, 2);
            var initial = state.Publish(marker, id, true);
            Check(state.Accept(new[] { Toggle(initial, true) }, new[] { initial }, false) == 0, "Disabled sharing accepted a checkmark.");
            Check(state.Accept(new[] { Toggle(initial, true) }, new[] { initial }, true) == 1, "Managed checkmark transition not accepted.");
            var checkedPin = state.Publish(marker, id, true);
            Check(checkedPin.Checked && Math.Abs(checkedPin.X - initial.X) + Math.Abs(checkedPin.Z - initial.Z) > 1.1, "Checkmark publication did not escape native matching range.");
            Check(state.Accept(new[] { initial }, new[] { initial }, true) == 0 && state.Publish(marker, id, true).Checked, "An unchanged stale table cleared a checkmark.");
            Check(state.Save(), "Shared checkmark publication was not saved.");
            var adoption = new SharedMapPinState(directory, 47);
            var issued = adoption.Publish(marker, id, true);
            var adoptedPin = new MapPin(777, issued.Name, issued.X, issued.Y, issued.Z, issued.Icon, true, "Steam_777");
            Check(adoption.Accept(new[] { adoptedPin }, new[] { issued }, true) == 1 && adoption.Adopted(adoptedPin) &&
                adoption.Publish(marker, id, true).Checked, "Vanilla grey-to-white ownership adoption lost the dungeon X.");
            adoption.Save(); var adoptedRestart = new SharedMapPinState(directory, 47); adoptedRestart.Load();
            Check(adoptedRestart.Adopted(adoptedPin), "Native adoption matching was lost on restart.");
            var ordinary = new MapPin(888, issued.Name, issued.X, issued.Y, issued.Z, issued.Icon);
            var preserve = new SharedMapPinState(directory, 48); preserve.Publish(marker, id, true);
            preserve.Accept(new[] { Toggle(ordinary, true), issued }, new[] { ordinary, issued }, true);
            Check(!preserve.Adopted(ordinary) && !preserve.Publish(marker, id, true).Checked, "An unrelated existing player hammer was treated as a managed dungeon pin.");
            var restart = new SharedMapPinState(directory, 45); restart.Load();
            Check(restart.Publish(marker, id, true).Checked, "Checkmark lost on restart.");
            Check(!restart.Publish(marker, id, false).Checked && restart.Publish(marker, id, true).Checked, "Disablement erased stored checkmark preference.");
            var current = restart.Publish(marker, id, true);
            Check(restart.Accept(new[] { Toggle(current, false) }, new[] { current }, true) == 1 && !restart.Publish(marker, id, true).Checked, "Explicit uncheck was not accepted.");
            var other = new MapPin(SharedMapTableData.DungeonOwner, "Invented", 100, 1, 100, 2, true);
            Check(restart.Accept(new[] { other }, Array.Empty<MapPin>(), true) == 0, "Unissued managed marker acquired a checkmark.");
            var personal = new MapPin(77, "My hammer", 100, 1, 100, 2, false, "Steam_77");
            var personalInitial = restart.Publish(personal, null, true);
            restart.Accept(new[] { Toggle(personalInitial, true) }, new[] { personalInitial }, true);
            var personalPublished = restart.Publish(personal, null, true);
            Check(personalPublished.Checked && personalPublished.Owner == 77 && personalPublished.Author == "Steam_77" && personalPublished.Name == "My hammer", "Player pin identity was not preserved.");
            Check(restart.Publish(personalPublished, null, true).X == personalPublished.X, "A reupload accumulated publication offsets.");
            var map = new SharedMapExploration(64, 12, 50, .25f);
            var managed = new[] { new MapMarker(marker.Name, marker.X, marker.Y, marker.Z, id) };
            byte[] initialTable = SharedMapTableData.Merge(map, null, out _, dungeons: managed,
                publication: (p, key) => restart.Publish(p, key, true))!;
            var tablePin = SharedMapTableData.ReadPins(64, initialTable).Single();
            restart.Accept(new[] { Toggle(tablePin, true) }, new[] { tablePin }, true);
            byte[] checkedTable = SharedMapTableData.Merge(map, initialTable, out _, dungeons: managed,
                publication: (p, key) => restart.Publish(p, key, true))!;
            Check(SharedMapTableData.ReadPins(64, checkedTable).Single().Checked, "Table normalization discarded accepted managed checkmarks.");
            Check(SharedMapTableData.Merge(map, checkedTable, out _, dungeons: managed,
                publication: (p, key) => restart.Publish(p, key, true)) == null, "Unchanged shared checkmarks caused recurring table writes.");
            byte[] privateTable = SharedMapTableData.Merge(map, checkedTable, out _, dungeons: managed,
                publication: (p, key) => restart.Publish(p, key, false))!;
            Check(!SharedMapTableData.ReadPins(64, privateTable).Single().Checked, "Disabled sharing left a shared X in table data.");
            restart.Save(); byte[] corrupt = File.ReadAllBytes(restart.FilePath); corrupt[^1] ^= 1; File.WriteAllBytes(restart.FilePath, corrupt);
            try { new SharedMapPinState(directory, 45).Load(); throw new Exception("Corrupt pin state accepted."); } catch (InvalidDataException) { }
            System.Console.WriteLine("PASS: remaining percentages, unknown/missing inventory, legacy/restart persistence, opt-in checks, stale-table resistance, explicit unchecks, issued-marker identity and player-pin preservation.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
