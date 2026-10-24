using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapPinChangesTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var vile = new MapPin(12, "Vile", 10, 2, 20, 3, false, "Steam_12");
        var home = new MapPin(13, "Home", 30, 2, 40, 1, false, "Steam_13");
        var removed = SharedMapPinChanges.Compare(new[] { vile, home }, new[] { home });
        Check(removed.Removed.Single().Name == "Vile" && removed.Removed[0].Owner == 12 &&
            removed.Added.Count == 0 && removed.Updated.Count == 0, "Ordinary Vile removal was missed or lost its creator.");
        var restored = SharedMapPinChanges.Compare(new[] { home }, new[] { vile, home });
        Check(restored.Added.Single().Name == "Vile" && restored.Removed.Count == 0, "A later upload restoring Vile was missed.");
        Check(!SharedMapPinChanges.Compare(new[] { vile, home }, new[] { home, vile }).Any, "Pin ordering invented changes.");
        var checkChanged = new MapPin(12, "Vile", 10, 2, 20, 3, true, "Steam_12");
        var updated = SharedMapPinChanges.Compare(new[] { vile }, new[] { checkChanged });
        Check(updated.Updated.Count == 1 && !updated.Updated[0].Before.Checked && updated.Updated[0].After.Checked &&
            updated.Added.Count == 0 && updated.Removed.Count == 0, "Checking a pin was misclassified as deletion.");
        var authorChanged = new MapPin(12, "Vile", 10, 2, 20, 3, true, "Steam_99");
        Check(SharedMapPinChanges.Compare(new[] { checkChanged }, new[] { authorChanged }).Updated.Single().After.Author == "Steam_99",
            "Author change was missed.");
        var moved = new MapPin(12, "Vile", 11, 2, 20, 3);
        var renamed = new MapPin(12, "Troll", 10, 2, 20, 3);
        var otherOwner = new MapPin(99, "Vile", 10, 2, 20, 3);
        var otherIcon = new MapPin(12, "Vile", 10, 2, 20, 1);
        foreach (var replacement in new[] { moved, renamed, otherOwner, otherIcon })
        {
            var change = SharedMapPinChanges.Compare(new[] { vile }, new[] { replacement });
            Check(change.Removed.Count == 1 && change.Added.Count == 1 && change.Updated.Count == 0,
                "A changed identity was guessed to be the same pin.");
        }
        var duplicate = SharedMapPinChanges.Compare(new[] { vile, vile, home }, new[] { vile, home });
        Check(duplicate.Removed.Count == 1 && duplicate.Added.Count == 0, "Duplicate-pin multiplicity hid a removal.");
        var metadataDuplicates = SharedMapPinChanges.Compare(new[] { vile, checkChanged }, new[] { checkChanged });
        Check(metadataDuplicates.Removed.Count == 1 && !metadataDuplicates.Removed[0].Checked && metadataDuplicates.Updated.Count == 0,
            "Metadata matching paired the wrong duplicate pin.");
        var boundary = SharedMapPinChanges.Compare(new[] { vile }, Array.Empty<MapPin>());
        Check(boundary.Removed.Count == 1, "An empty valid table lost its last pin removal.");
        Check(!SharedMapPinChanges.Compare(Array.Empty<MapPin>(), Array.Empty<MapPin>()).Any, "Empty snapshots invented changes.");
        System.Console.WriteLine("PASS: ordinary pin removal/restoration, ownership, metadata, duplicates and empty snapshots.");
    }
}
