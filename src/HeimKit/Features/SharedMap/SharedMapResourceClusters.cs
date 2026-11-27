using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public static class SharedMapResourceClusters
{
    public static IReadOnlyList<MapMarker> Group(IEnumerable<ResourceDeposit> discoveries, float distance, bool removeDepleted,
        Action<int, int?, string>? association = null, Func<ResourceDeposit, bool>? pillarVerified = null,
        Action<ResourceDeposit, int>? membership = null)
    {
        var ordered = discoveries.OrderBy(d => d.Identity).ToArray();
        var groups = SharedMapMarkerClusters.Group(ordered.Where(d => d.Kind != ResourceKind.SoftTissue &&
            (d.Kind != ResourceKind.FlametalPillar || !d.Depleted && pillarVerified?.Invoke(d) == true) &&
            (d.Kind != ResourceKind.Silver || d.SilverDiscovered)), distance,
            d => d.X, d => d.Z, (a, b) => a.Kind == b.Kind && a.Kind != ResourceKind.AncientRoot && a.Kind != ResourceKind.FlametalPillar);
        var tissues = ordered.Where(d => d.Kind == ResourceKind.SoftTissue && !d.Depleted).ToArray();
        var skullCells = new Dictionary<(int, int), List<ResourceDeposit>>();
        if (tissues.Length > 0)
        {
            foreach (var skull in ordered)
            {
                if (!(skull.Skull is SkullBounds bounds)) continue;
                for (int x = Cell(bounds.MinX); x <= Cell(bounds.MaxX); x++)
                    for (int z = Cell(bounds.MinZ); z <= Cell(bounds.MaxZ); z++)
                    {
                        if (!skullCells.TryGetValue((x, z), out var candidates))
                            skullCells.Add((x, z), candidates = new List<ResourceDeposit>());
                        candidates.Add(skull);
                    }
            }
        }
        var tissueHosts = new Dictionary<int, int>();
        var publishedGroups = new Dictionary<int, int>();
        var bySkull = new Dictionary<int, ResourceDeposit>();
        var markers = new List<MapMarker>();
        foreach (var tissue in tissues)
        {
            ResourceDeposit? match = null;
            bool ambiguous = false;
            if (skullCells.TryGetValue((Cell(tissue.X), Cell(tissue.Z)), out var candidates))
                foreach (var candidate in candidates)
                {
                    if (!candidate.Skull!.Value.Contains(tissue)) continue;
                    if (match != null) { ambiguous = true; break; }
                    match = candidate;
                }
            if (match != null && !ambiguous)
            {
                if (membership != null) tissueHosts[tissue.Identity] = match.Identity;
                if (!bySkull.ContainsKey(match.Identity)) bySkull.Add(match.Identity, tissue);
                association?.Invoke(tissue.Identity, match.Identity, "matched");
            }
            else
            {
                association?.Invoke(tissue.Identity, null, ambiguous ? "ambiguous_skulls" : "no_observed_skull");
                membership?.Invoke(tissue, tissue.Identity);
                AddMarker("(ST)", tissue.X, tissue.Y, tissue.Z, tissue.Identity);
            }
        }
        foreach (var group in groups)
        {
            ResourceDeposit? anchor = null;
            foreach (var member in group)
                if (bySkull.TryGetValue(member.Identity, out var tissue) && (anchor == null || tissue.Identity < anchor.Identity))
                    anchor = tissue;
            var remaining = group.Where(d => !removeDepleted || !d.Depleted).ToArray();
            if (remaining.Length == 0 && anchor == null) continue;
            if (membership != null)
                foreach (var member in group)
                {
                    membership(member, group[0].Identity);
                    publishedGroups[member.Identity] = group[0].Identity;
                }
            string name = group[0].Kind == ResourceKind.AncientRoot
                ? SharedMapAncientRoot.Label(group[0]) : group[0].Kind == ResourceKind.FlametalPillar ? "Flametal Pillar" : group[0].Kind.ToString();
            if (SharedMapResourceHealth.TracksProgress(group[0].Kind)) name = PercentageLabel(group);
            if (group[0].Kind == ResourceKind.Tin && remaining.Length > 1)
                name += " (" + remaining.Length.ToString(CultureInfo.InvariantCulture) + ")";
            if (anchor != null)
            {
                name = group.All(d => d.Depleted) ? "(ST)" : name + " (ST)";
                AddMarker(name, anchor.X, anchor.Y, anchor.Z, group[0].Identity);
            }
            else AddMarker(name, (float)remaining.Average(d => (double)d.X), (float)remaining.Average(d => (double)d.Y),
                (float)remaining.Average(d => (double)d.Z), group[0].Identity);
        }
        void AddMarker(string name, float x, float y, float z, int id)
        {
            if (markers.Count >= 4096) throw new InvalidOperationException("Community resource marker limit exceeded; table data was not replaced.");
            markers.Add(new MapMarker(name, x, y, z, id.ToString(CultureInfo.InvariantCulture)));
        }
        if (membership != null)
            foreach (var tissue in tissues)
                if (tissueHosts.TryGetValue(tissue.Identity, out int skull) && publishedGroups.TryGetValue(skull, out int group))
                    membership(tissue, group);
        return markers;
    }

    private static int Cell(float coordinate) => (int)Math.Floor(coordinate / 32d);

    private static string PercentageLabel(IReadOnlyList<ResourceDeposit> group)
    {
        string label = group[0].Kind == ResourceKind.BlackMarble ? "Black Marble" : group[0].Kind.ToString();
        if (group.All(d => d.Depleted)) return label + " 0%";
        if (group.Any(d => !d.Progress.HasValue)) return label + " ?%";
        // Include depleted members' original totals. Otherwise mining a whole boulder
        // could make the remaining group's percentage jump back to 100%.
        long total = group.Sum(d => (long)d.Progress!.Value.TotalSections);
        long remaining = group.Sum(d => d.Depleted ? 0L : d.Progress!.Value.RemainingSections);
        long percent = (remaining * 100 + total - 1) / total;
        return label + " " + percent.ToString(CultureInfo.InvariantCulture) + "%";
    }
}
