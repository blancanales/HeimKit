using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.AFKNightWatch;
internal static class SleepDelayTests
{
    public static void Run()
    {
        var delay = new AFKNightWatchSleepDelay();
        Check(!delay.Allow(100, true, false, 30), "Threshold started sleep immediately");
        Check(!delay.Allow(129.9f, true, false, 30), "Sleep started before full grace");
        Check(delay.Allow(130, true, false, 30), "Full grace did not authorize sleep");
        Check(!delay.Allow(131, false, false, 30), "Lost quorum authorized sleep");
        Check(!delay.Allow(140, true, false, 30), "Lost quorum retained expired grace");
        Check(delay.Allow(145, true, true, 30), "Everyone in bed did not bypass grace");
        Check(!delay.Allow(146, true, false, 30), "Unanimity left an expired timer");
        delay.Reset();
        Check(!delay.Allow(500, true, false, 30), "Reset retained grace");
        Check(!delay.Allow(400, true, false, 30), "Clock rollback authorized sleep");
        Check(delay.Allow(430, true, false, 30), "Clock rollback failed to restart grace");
        Check(!delay.Allow(float.NaN, true, false, 30), "Unknown clock authorized sleep");
        Check(!delay.Allow(600, true, false, 30), "Unknown clock retained grace");
        delay.Reset(); Check(delay.Allow(0, true, false, 0), "Zero delay was not supported");
        System.Console.WriteLine("PASS: continuous sleep grace, quorum loss/reset, unanimous early start, clock rollback and zero delay.");
    }
    private static void Check(bool pass, string reason) { if (!pass) throw new Exception(reason); }
}
