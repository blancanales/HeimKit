using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardExposureTrackingTests
{
    public static void Run()
    {
        var tracking = new WeatherGuardExposureTracking();
        ZDOID[] ids = Enumerable.Range(0, 61).Select(i => new ZDOID(1, (uint)i)).ToArray();
        for (int i = 0; i < ids.Length; i++)
            tracking.Observe(ids[i], i >= 3, false, true, true, "Swamp", 10);
        tracking.Retain(new HashSet<ZDOID>(ids), new HashSet<ZDOID>(ids));
        var counts = tracking.Counts(61, 11);
        if (!Equals(counts["roof_sample_covered"], 58) || !Equals(counts["roof_sample_exposed"], 3) ||
            !Equals(counts["roof_sample_exposed_managed"], 3) || !Equals(counts["roof_unchecked_or_stale"], 0) ||
            !Equals(counts["roof_sample_cache_disagreements"], 3) || !Equals(counts["roof_sample_biomes"], "Swamp:61"))
            throw new Exception("Whole-scope exposure counts are not independent of the three-item debug sample or cached roof.");
        tracking.Retain(new HashSet<ZDOID>(ids), new HashSet<ZDOID>());
        if (!Equals(tracking.Counts(61, 15)["roof_sample_exposed_waiting"], 3))
            throw new Exception("Lost processing ownership still counts as managed.");
        tracking.Retain(new HashSet<ZDOID>(ids), new HashSet<ZDOID>(ids));
        var restored = tracking.Counts(61, 16);
        if (!Equals(restored["roof_sample_exposed_managed"], 3) || !Equals(restored["roof_sample_exposed_waiting"], 0) ||
            !Equals(restored["roof_sample_oldest_check_age_s"], 6f))
            throw new Exception("Reacquisition did not restore current management without changing the geometry timestamp.");
        if (!Equals(tracking.Counts(61, 50)["roof_unchecked_or_stale"], 61))
            throw new Exception("Stale geometry appears to be current.");
        tracking.Retain(new HashSet<ZDOID>(), new HashSet<ZDOID>());
        if (!Equals(tracking.Counts(0, 51)["roof_sample_size"], 0)) throw new Exception("Departed scope retained counts.");
        var large = new WeatherGuardExposureTracking();
        for (int i = 0; i < 360; i++) large.Observe(new ZDOID(1, (uint)i), i >= 7, false, true, true, "Meadows", 10);
        var sample = large.Counts(1765, 11);
        if (!Equals(sample["roof_sample_size"], 360) || !Equals(sample["roof_unchecked_or_stale"], 1405) ||
            !Equals(sample["roof_sample_covered"], 353) || !Equals(sample["roof_sample_exposed"], 7) ||
            sample.ContainsKey("eligible_player_pieces_now") || sample.ContainsKey("covered_now") || sample.ContainsKey("exposed_now"))
            throw new Exception("Partial roof census still appears to describe the full eligible scope or duplicates eligibility.");
        System.Console.WriteLine("PASS: whole-scope roof/biome counts, ownership loss and stale/unknown geometry; geometry does not produce prevention notices.");
    }
}
