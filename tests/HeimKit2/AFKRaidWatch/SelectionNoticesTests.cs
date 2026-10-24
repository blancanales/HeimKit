using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Features.AFKRaidWatch;
internal static class SelectionNoticesTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Raid batch notice checks");
        var messages = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        var notices = new AFKRaidWatchSelectionNotices(log);
        notices.Excluded(); notices.End(false); notices.Begin(); notices.End(false);
        if (messages.Count != 0) throw new Exception("Outside or empty selections emitted Info.");
        notices.Begin(); for (int i = 0; i < 32; i++) notices.Excluded();
        if (messages.Count != 0) throw new Exception("Per-candidate normal logging bypassed batching.");
        notices.End(false); notices.End(false);
        if (messages.Count != 1 || messages[0].Text != "AFKRaidWatch: Excluded 32 automatic raid candidates at AFK-only locations." || messages[0].Level != LogLevel.Info)
            throw new Exception("Candidate exclusions were not one debug-off batch.");
        notices.Begin(); notices.Excluded(); notices.End(true);
        if (messages.Count != 1) throw new Exception("Failed selection reported successful outcome.");
        notices.Begin(); notices.Excluded(); notices.Reset(); notices.End(false);
        if (messages.Count != 1) throw new Exception("Paused/failed module emitted stale batch.");
        notices.Begin(); notices.Excluded(); notices.End(false);
        if (messages[^1].Text != "AFKRaidWatch: Excluded 1 automatic raid candidate at AFK-only locations.") throw new Exception("Next selection inherited prior exclusions.");
        System.Console.WriteLine("PASS: grouped Info automatic candidate exclusions, singular wording, quiet empty/explicit scope and cleared failed/paused selection batches.");
    }
}
