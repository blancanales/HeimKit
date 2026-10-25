using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
internal static class InfiniteTorchesStandardNoticesTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Grouped configuration warning checks");
        var messages = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        var torch = new InfiniteTorchesDiagnostics(() => false, log);
        torch.UnsupportedPrefabs(32, new Dictionary<string, int> { ["prefab not found"] = 30, ["no Fireplace component"] = 2 });
        torch.UnsupportedPrefabs(0, new Dictionary<string, int>());
        if (messages.Count != 1 || messages[0].Level != LogLevel.Warning || !messages[0].Text.Contains("Skipped 32") || !messages[0].Text.Contains("prefab not found (30)") || !messages[0].Text.Contains("no Fireplace component (2)") || !messages[0].Text.Contains("Review Torches.Prefabs"))
            throw new Exception("Unsupported prefabs emitted multiple/no-action warning entries.");
        System.Console.WriteLine("PASS: one unsupported-prefab warning with grouped reasons/counts and a useful configuration action.");
    }
}
