using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardExposureTrackingTests
{
    public static void Run()
    {
        var tracking = new WeatherGuardExposureTracking();
        string[] ids = Enumerable.Range(0, 61).Select(i => i.ToString()).ToArray();
        for (int i = 0; i < ids.Length; i++)
            tracking.Observe(ids[i], i >= 3, false, true, true, "Swamp", 10);
        tracking.Retain(ids, _ => true);
        var counts = tracking.Counts(61, 11);
        if (!Equals(counts["roof_sample_covered_now"], 58) || !Equals(counts["roof_sample_exposed_now"], 3) ||
            !Equals(counts["roof_sample_exposed_managed_now"], 3) || !Equals(counts["roof_unchecked_or_stale_now"], 0) ||
            !Equals(counts["roof_sample_cache_disagreements_now"], 3) || !Equals(counts["roof_sample_biomes"], "Swamp:61"))
            throw new Exception("Whole-scope exposure counts are not independent of the three-item debug sample or cached roof.");
        tracking.Retain(ids, _ => false);
        if (!Equals(tracking.Counts(61, 15)["roof_sample_exposed_waiting_now"], 3))
            throw new Exception("Lost processing ownership still counts as managed.");
        tracking.Retain(ids, _ => true);
        var restored = tracking.Counts(61, 16);
        if (!Equals(restored["roof_sample_exposed_managed_now"], 3) || !Equals(restored["roof_sample_exposed_waiting_now"], 0) ||
            !Equals(restored["roof_sample_oldest_check_age_s"], 6f))
            throw new Exception("Reacquisition did not restore current management without changing the geometry timestamp.");
        if (!Equals(tracking.Counts(61, 50)["roof_unchecked_or_stale_now"], 61))
            throw new Exception("Stale geometry appears to be current.");
        tracking.Retain(Array.Empty<string>(), _ => false);
        if (!Equals(tracking.Counts(0, 51)["roof_sample_size_now"], 0)) throw new Exception("Departed scope retained counts.");
        var large = new WeatherGuardExposureTracking();
        for (int i = 0; i < 360; i++) large.Observe(i.ToString(), i >= 7, false, true, true, "Meadows", 10);
        var sample = large.Counts(1765, 11);
        if (!Equals(sample["roof_sample_size_now"], 360) || !Equals(sample["roof_unchecked_or_stale_now"], 1405) ||
            !Equals(sample["roof_sample_covered_now"], 353) || !Equals(sample["roof_sample_exposed_now"], 7) ||
            sample.ContainsKey("eligible_player_pieces_now") || sample.ContainsKey("covered_now") || sample.ContainsKey("exposed_now"))
            throw new Exception("Partial roof census still appears to describe the full eligible scope or duplicates eligibility.");
        System.Console.WriteLine("PASS: whole-scope roof/biome counts, ownership loss and stale/unknown geometry; geometry does not produce prevention notices.");
    }
}
