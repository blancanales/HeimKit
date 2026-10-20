using BepInEx.Configuration;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapResourcePinSpacingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static SharedMapExploration Map() => new(64, 12, 50, .25f);
    private static MapMarker Marker(float x, string name = "Copper") => new(name, x, 1, 0);
    public static void Run()
    {
        foreach (long owner in new[] { SharedMapTableData.PortalOwner, SharedMapTableData.DungeonOwner,
            SharedMapTableData.RunestoneOwner, SharedMapTableData.SpawnerOwner })
        {
            var pins = new List<MapPin> {
                new(owner, "Landmark", -1, 1000, 0, 3),
                new(SharedMapTableData.ResourceOwner, "Copper", 9, -1000, 0, 3),
                new(SharedMapTableData.ResourceOwner, "Tin", 9.01f, 1, 0, 3),
                new(77, "Personal note", 9, 1, 0, 3, true, "author") };
            Check(SharedMapResourcePinSpacing.Apply(pins, 10) == 1 && pins.Count == 3 && pins.Any(p => p.Name == "Tin") &&
                pins.Any(p => p.Owner == 77 && p.Checked && p.Author == "author"),
                "Spacing lost its inclusive horizontal boundary, family priority or personal-pin preservation.");
        }
        var resources = new List<MapPin> {
            new(SharedMapTableData.ResourceOwner, "Copper", 0, 1, 0, 3),
            new(SharedMapTableData.ResourceOwner, "Tin", 0, 1, 0, 3), new(77, "Note", 0, 1, 0, 3) };
        Check(SharedMapResourcePinSpacing.Apply(resources, 10) == 0 && resources.Count == 3,
            "Resources or arbitrary personal pins hid another resource.");
        var map = Map(); var ore = new[] { Marker(10) }; var tomb = new[] { Marker(0, "Burial Chamber") };
        byte[] hidden = SharedMapTableData.Merge(map, null, out _, resources: ore, dungeons: tomb)!;
        Check(SharedMapTableData.ReadPins(64, hidden).Single().Owner == SharedMapTableData.DungeonOwner && ore.Length == 1,
            "Default ten-metre spacing failed or mutated the resource catalog.");
        Check(SharedMapTableData.Merge(map, hidden, out _, resources: ore, dungeons: tomb) == null,
            "Unchanged suppressed publications caused repeated table writes.");
        byte[] shown = SharedMapTableData.Merge(map, hidden, out _, resources: ore, dungeons: tomb, resourceMarkerHideDistanceMeters: 0)!;
        Check(SharedMapTableData.ReadPins(64, shown).Count == 2, "Zero did not disable suppression and restore the resource.");
        byte[] rehidden = SharedMapTableData.Merge(map, shown, out _, resources: ore, dungeons: tomb)!;
        Check(SharedMapTableData.ReadPins(64, rehidden).Count == 1, "A stale shared-resource upload bypassed suppression.");
        byte[] noLandmark = SharedMapTableData.Merge(map, hidden, out _, resources: ore, dungeons: Array.Empty<MapMarker>())!;
        Check(SharedMapTableData.ReadPins(64, noLandmark).Single().Owner == SharedMapTableData.ResourceOwner,
            "Resource stayed hidden after its blocking marker was removed/disabled.");
        byte[] excluded = SharedMapTableData.Merge(Map(), null, out _, resources: ore, dungeons: tomb,
            removePin: p => p.Owner == SharedMapTableData.DungeonOwner)!;
        Check(SharedMapTableData.ReadPins(64, excluded).Single().Owner == SharedMapTableData.ResourceOwner,
            "An excluded landmark still hid resources.");
        byte[] moved = SharedMapTableData.Merge(Map(), null, out _, resources: new[] { Marker(0) }, dungeons: tomb,
            publication: (p, _) => p.Owner == SharedMapTableData.DungeonOwner
                ? new MapPin(p.Owner, p.Name, 20, p.Y, p.Z, p.Icon) : p)!;
        Check(SharedMapTableData.ReadPins(64, moved).Count == 2, "Spacing used source points instead of final publication positions.");
        byte[] small = SharedMapTableData.Merge(Map(), null, out _, resources: ore, dungeons: tomb, resourceMarkerHideDistanceMeters: 5)!;
        Check(SharedMapTableData.ReadPins(64, small).Count == 2, "Configured distance was ignored.");
        foreach (float invalid in new[] { -1f, 101f, float.NaN, float.PositiveInfinity })
        {
            bool rejected = false;
            try { SharedMapResourcePinSpacing.Apply(new(), invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Invalid spacing distance was accepted.");
        }
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-spacing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "heimkit.cfg");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var settings = new SharedMapSettings(config);
            Check(settings.ResourceMarkerHideDistanceMeters.Value == 10, "Resource spacing default is not ten metres.");
            settings.ResourceMarkerHideDistanceMeters.Value = 25; config.Save();
            var reloaded = new SharedMapSettings(new ConfigFile(path, false) { SaveOnConfigSet = false });
            Check(reloaded.ResourceMarkerHideDistanceMeters.Value == 25, "Reload overwrote configured resource spacing.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: configurable resource publication spacing, all landmark families, horizontal boundary, zero disablement, repeat uploads, offsets, restoration and personal-pin preservation.");
    }
}
