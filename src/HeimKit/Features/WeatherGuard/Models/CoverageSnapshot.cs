using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;

namespace HeimKit.Features.WeatherGuard.Models;

public sealed class CoverageSnapshot
{
    public string State { get; set; } = "not_observed";
    public bool Observed { get; set; }
    public float? ScanAge { get; set; }
    public int NearbyRecords { get; set; }
    public int DistantRecords { get; set; }
    public int Regions { get; set; }
    public bool Limited { get; set; }
    public bool RainEnabled { get; set; }
    public bool UnderwaterEnabled { get; set; }
    public int Eligible { get; private set; }
    public int Protected { get; private set; }
    public int Skipped { get; private set; }
    public int Waiting { get; private set; }
    public int UnloadedEligible { get; private set; }
    public Dictionary<string, int> SkipReasons { get; } = new();
    public Dictionary<string, int> WaitReasons { get; } = new();
    public void Add(string eligibility, bool managed, bool loaded, string waitReason)
    {
        static void Count(Dictionary<string, int> counts, string reason)
        { counts.TryGetValue(reason, out int count); counts[reason] = count + 1; }
        if (eligibility != "eligible") { Skipped++; Count(SkipReasons, eligibility); return; }
        Eligible++;
        if (managed && loaded) Protected++;
        else { Waiting++; if (!loaded) UnloadedEligible++; Count(WaitReasons, !loaded ? "waiting_instance" : waitReason); }
    }
}
