using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapPortalRefreshTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static PortalMarker Marker(string name, string id = "portal-a", float x = 0) => new(name, x, 3, 0, id);
    private static double Distance(PortalMarker a, PortalMarker b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    public static void Run()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "portal-refresh-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            int changes = 0;
            var refresh = new SharedMapPortalRefresh(directory, 123, (_, _, _, _) => changes++);
            Check(!refresh.Load() && !refresh.Save(), "Unused portal refresh state wrote a file.");
            var collision = new SharedMapPortalRefresh(directory, 789);
            var collisionActual = Marker("China");
            collision.Apply(new[] { collisionActual });
            Check(collision.AvoidObsoletePin("CHINA", 0, 0), "Stale-dot collision did not reposition the shared portal.");
            var corrected = collision.Apply(new[] { collisionActual }).Single();
            Check(Distance(corrected, collisionActual) > 1.1 && Distance(corrected, collisionActual) <= 3,
                "Corrected publication cannot replace the stale shared pin or escaped cleanup range.");
            Check(!collision.AvoidObsoletePin("China", 0, 0), "Repeated stale uploads moved the marker again.");
            Check(collision.Save(), "Collision publication did not persist.");
            var collisionReload = new SharedMapPortalRefresh(directory, 789);
            Check(collisionReload.Load() && Distance(corrected, collisionReload.Apply(new[] { collisionActual }).Single()) == 0,
                "Restart lost the icon-correction publication position.");
            var initial = refresh.Apply(new[] { Marker("NoName") }).Single();
            Check(initial.X == 0 && initial.Z == 0 && initial.Y == 3, "New marker did not start at the actual portal.");
            var actual = Marker("Home");
            var renamed = refresh.Apply(new[] { actual }).Single();
            Check(Distance(initial, renamed) > 1.1 && Distance(actual, renamed) <= Math.Sqrt(8), "Rename was not separated or drifted beyond the bounded grid.");
            Check(actual.X == 0 && actual.Z == 0 && renamed.Name == "Home", "Refresh changed actual coordinates or lost the name.");
            Check(Distance(renamed, refresh.Apply(new[] { actual }).Single()) == 0 && changes == 2, "Repeated table preparations moved unchanged markers or emitted duplicate changes.");
            Check(refresh.Save(), "Portal publication state did not persist.");
            byte[] firstSave = File.ReadAllBytes(refresh.FilePath);
            var removals = new List<string>();
            var restart = new SharedMapPortalRefresh(directory, 123, removed: (id, name) => removals.Add(id + ":" + name));
            Check(restart.Load() && Distance(renamed, restart.Apply(new[] { actual }).Single()) == 0 && !restart.Save(), "Restart moved an unchanged marker or rewrote unchanged state.");
            var before = renamed;
            for (int i = 0; i < 20; i++)
            {
                var next = restart.Apply(new[] { Marker("Rename " + i) }).Single();
                Check(Distance(before, next) > 1.1 && Distance(Marker(""), next) <= Math.Sqrt(8), "Repeated renames reused the immediately previous position or drifted.");
                before = next;
            }
            Check(restart.Save() && File.ReadAllBytes(restart.FilePath + ".bak").SequenceEqual(firstSave), "State replacement lost the previous backup.");
            var group = restart.Apply(new[] { Marker("Multi Portals (2)", x: 1) }).Single();
            Check(Distance(before, group) > 1.1, "Group label transition was blocked by an overlapping prior publication.");
            var empty = restart.Apply(Array.Empty<PortalMarker>());
            Check(empty.Count == 0 && restart.Count == 0 && restart.Save(), "Deleted markers were not pruned and persisted.");
            Check(removals.SequenceEqual(new[] { "portal-a:Multi Portals (2)" }), "Removal evidence lost the marker identity or prior label.");
            restart.Apply(Array.Empty<PortalMarker>());
            Check(removals.Count == 1, "Unchanged empty catalog repeated removal evidence.");
            var emptyReload = new SharedMapPortalRefresh(directory, 123);
            Check(emptyReload.Load() && emptyReload.Count == 0, "Persisted empty refresh state was invalid.");

            bool wrongWorld = false;
            string otherPath = new SharedMapPortalRefresh(directory, 456).FilePath;
            File.Copy(restart.FilePath, otherPath);
            try { new SharedMapPortalRefresh(directory, 456).Load(); }
            catch (InvalidDataException) { wrongWorld = true; }
            Check(wrongWorld, "Portal refresh state was accepted for a different world.");
            byte[] corrupt = File.ReadAllBytes(restart.FilePath); corrupt[0] ^= 1;
            File.WriteAllBytes(restart.FilePath, corrupt);
            bool rejected = false;
            try { new SharedMapPortalRefresh(directory, 123).Load(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(restart.FilePath).SequenceEqual(corrupt), "Damaged refresh state was accepted or replaced.");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Completed refresh saves leaked temporary files.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: portal name refresh separation, bounded offsets, unchanged stability, restart persistence, grouping transitions, pruning, atomic backup and world/corruption rejection.");
    }
}
