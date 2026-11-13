using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKNightWatch.Models;
internal static class SleepNoticesTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Sleep outcome notice checks");
        var messages = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        int evidence = 0;
        var notices = new AFKNightWatchSleepNotices(log, _ => evidence++);
        var vote = new SleepDecision { Participants = 3, Sleepers = 2, Required = 2, Met = true, AllowedAfterDelay = true };
        notices.Observe(false, false);
        for (int i = 0; i < 32; i++) notices.Observe(false, false, vote);
        if (messages.Count != 0) throw new Exception("Votes without sleep start announced success.");
        notices.Observe(true, true, vote);
        for (int i = 0; i < 32; i++) notices.Observe(true, true, vote);
        notices.Observe(false, false);
        if (messages.Count != 1 || evidence != 1 || messages[0].Text != "AFKNightWatch: Sleep started under the configured rules while some players were not in bed." || messages[0].Level != LogLevel.Info)
            throw new Exception("Intervention was not one normal notice per confirmed sleep.");
        var unanimous = new SleepDecision { Participants = 3, Sleepers = 3, Met = true, AllowedAfterDelay = true };
        notices.Observe(true, true, unanimous); notices.Observe(false, false);
        notices.Observe(true, true); notices.Observe(false, false);
        vote.AllowedAfterDelay = false;
        notices.Observe(true, true, vote); notices.Observe(false, false);
        vote.AllowedAfterDelay = true; vote.Unknown = true;
        notices.Observe(true, true, vote); notices.Observe(false, false);
        if (messages.Count != 1) throw new Exception("Native, unanimous, waiting or unknown sleep was claimed as an intervention.");
        vote.Unknown = false;
        notices.Observe(false, null); notices.Observe(true, true, vote);
        if (messages.Count != 1) throw new Exception("Unknown baseline claimed new sleep.");
        notices.Reset(); notices.Observe(false, false);
        var excluded = new SleepDecision { Participants = 1, Sleepers = 1, ExcludedIdle = 1, Met = true, AllowedAfterDelay = true };
        notices.Observe(true, true, excluded);
        if (messages.Count != 2) throw new Exception("AFK exclusion intervention was omitted.");
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var coordinator = typeof(AFKNightWatchSleepVoteCoordinator);
        var callback = coordinator.GetField("_lifecycle", hidden)!;
        object? prior = callback.GetValue(null); int called = 0;
        try
        {
            callback.SetValue(null, (Action<bool, bool?, SleepDecision?>)((_, __, ___) => called++));
            object?[] prefixArgs = { false, 0d, null };
            coordinator.GetMethod("UpdateEntered", hidden)!.Invoke(null, prefixArgs);
            if (called != 1 || prefixArgs[2] != null) throw new Exception("Normal observation requires debug or collects optional evidence.");
            var nativeError = new InvalidOperationException("native sleep failure");
            var returned = coordinator.GetMethod("UpdateFinished", hidden)!.Invoke(null, new object?[] { null, false, nativeError });
            if (called != 2 || !ReferenceEquals(returned, nativeError)) throw new Exception("Sleep logging altered native error propagation.");
        }
        finally { callback.SetValue(null, prior); }
        System.Console.WriteLine("PASS: confirmed non-unanimous intervention once, quiet native/unanimous/waiting/error states and preserved native exceptions.");
    }
}
