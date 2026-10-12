using BepInEx.Logging;
using HeimKit.Shared;

internal static class ModuleCleanupTests
{
    public static void Run()
    {
        using var logger = new ManualLogSource("Cleanup tests");
        var messages = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, entry) => messages.Add((entry.Level, entry.Data.ToString()!));
        var stopped = new List<string>();
        ModuleCleanup.Run(logger, "AFKNightWatch",
            () => { stopped.Add("raid"); throw new InvalidOperationException("raid removal failed"); },
            () => { stopped.Add("sleep"); throw new InvalidOperationException("sleep removal failed"); },
            () => stopped.Add("activity"));
        ModuleCleanup.Run(logger, "Infinite Torches", () => stopped.Add("torches"));
        if (!stopped.SequenceEqual(new[] { "raid", "sleep", "activity", "torches" }) ||
            messages.Count != 2 || messages.Any(m => m.Level != LogLevel.Error || !m.Text.StartsWith("AFKNightWatch: LIFECYCLE")))
            throw new Exception("Cleanup failure interrupted later steps/modules or lost error visibility.");
        ModuleCleanup.Run(logger, "HeimKit");
        System.Console.WriteLine("PASS: multiple cleanup failures retain error visibility and allow remaining registrations and independent modules to stop.");
    }
}
