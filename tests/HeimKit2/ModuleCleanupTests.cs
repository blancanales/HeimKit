using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;

internal static class ModuleCleanupTests
{
    public static void Run()
    {
        using var logger = new ManualLogSource("Cleanup tests");
        var messages = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, entry) => messages.Add((entry.Level, LogCapture.Text(entry.Data)));
        var stopped = new List<string>();
        bool failedCleanup = ModuleCleanup.Run(logger, "AFKNightWatch",
            () => { stopped.Add("raid"); throw new InvalidOperationException("raid removal failed"); },
            () => { stopped.Add("sleep"); throw new InvalidOperationException("sleep removal failed"); },
            () => stopped.Add("activity"));
        bool healthyCleanup = ModuleCleanup.Run(logger, "InfiniteTorches", () => stopped.Add("torches"));
        if (failedCleanup || !healthyCleanup || !stopped.SequenceEqual(new[] { "raid", "sleep", "activity", "torches" }) ||
            messages.Count != 2 || messages.Any(m => m.Level != LogLevel.Error || !m.Text.StartsWith("AFKNightWatch.lifecycle:")))
            throw new Exception("Cleanup failure interrupted later steps/modules or lost error visibility.");
        ModuleCleanup.Run(logger, "HeimKit");
        System.Console.WriteLine("PASS: multiple cleanup failures retain error visibility and allow remaining registrations and independent modules to stop.");
    }
}
