using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HeimKit.Features.WeatherGuard;

// Geometry observations cover the whole eligible scope, independently of Debug samples.
public sealed class WeatherGuardExposureTracking
{
    public const int MaximumObservationAgeSeconds = 30;
    public const int ProbeLimitPerScan = 12;
    private readonly Dictionary<ZDOID, (bool Roof, bool Water, bool Managed, bool CachedRoof, string Biome, float At)> _observed = new();
    private readonly List<ZDOID> _remove = new();
    public void Retain(HashSet<ZDOID> eligible, HashSet<ZDOID> managed)
    {
        _remove.Clear(); foreach (var pair in _observed) _remove.Add(pair.Key);
        foreach (var id in _remove)
            if (!eligible.Contains(id)) _observed.Remove(id);
            else { var item = _observed[id]; item.Managed = managed.Contains(id); _observed[id] = item; }
    }
    public void Observe(ZDOID id, bool roof, bool underwater, bool managed, bool cachedRoof, string biome, float now)
    {
        _observed[id] = (roof, underwater, managed, cachedRoof, biome, now);
    }
    public Dictionary<string, object?> Counts(int eligible, float now)
    {
        var fresh = _observed.Values.Where(item => now >= item.At && now - item.At <= MaximumObservationAgeSeconds).ToArray();
        return new()
        {
            ["roof_sample_size"] = fresh.Length, ["roof_unchecked_or_stale"] = Math.Max(0, eligible - fresh.Length),
            ["roof_sample_covered"] = fresh.Count(item => item.Roof), ["roof_sample_exposed"] = fresh.Count(item => !item.Roof),
            ["roof_sample_exposed_managed"] = fresh.Count(item => !item.Roof && item.Managed),
            ["roof_sample_exposed_waiting"] = fresh.Count(item => !item.Roof && !item.Managed),
            ["roof_sample_underwater"] = fresh.Count(item => item.Water),
            ["roof_sample_cache_disagreements"] = fresh.Count(item => item.Roof != item.CachedRoof),
            ["roof_sample_biomes"] = string.Join(",", fresh.GroupBy(item => item.Biome).OrderBy(group => group.Key).Select(group => group.Key + ":" + group.Count())),
            ["roof_sample_oldest_check_age_s"] = fresh.Length == 0 ? (float?)null : fresh.Max(item => now - item.At)
        };
    }
}
