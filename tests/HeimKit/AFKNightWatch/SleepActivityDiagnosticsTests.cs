using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKNightWatch.Models;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.AFKRaidWatch.Models;
internal static class SleepActivityDiagnosticsTests
{
    public static void Run()
    {
        DiagnosticLog.StartSession();
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "split-log-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var log = new ManualLogSource("Split diagnostic checks");
        var messages = new List<string>(); log.LogEvent += (_, e) => messages.Add(e.Data.ToString()!);
        var debug = DiagnosticsSettings.Bind(config); var policy = new DebugLoggingPolicy(debug, false);
        var sleep = new AFKNightWatchDiagnostics(log, policy); var raid = new AFKRaidWatchDiagnostics(log, policy);
        sleep.Report(0, () => throw new Exception("Debug-off sleep collection")); raid.Report(0, () => throw new Exception("Debug-off raid collection"));
        debug.Value = true;
        var state = new NightWatchStatus { Players = 3, Votes = 1, Vote = new SleepDecision { Participants = 3, Sleepers = 2, Required = 2, Met = true } };
        sleep.Report(0, () => state);
        if (!messages.Any(m => m.Contains("quorum_met_last_vote=true") && m.Contains("time_skip=not_observed")) || messages.Any(m => m.Contains("raid_"))) throw new Exception("Sleep evidence lost scope");
        int count = messages.Count;
        sleep.Report(59, () => throw new Exception("Throttled snapshot ran")); state.Votes = 100; sleep.Report(60, () => state);
        if (messages.Count != count) throw new Exception("Unchanged full snapshot repeated");
        sleep.Report(300, () => state);
        if (messages.Count != count + 1 || !messages[^1].Contains("Heartbeat")) throw new Exception("Compact heartbeat missing");
        sleep.Report(360, () => new NightWatchStatus { Players = 0 });
        if (messages.Count != count + 1) throw new Exception("Empty sleep scope logged");
        var rs = new RaidWatchStatus { Connected = 3, Candidates = 1, Suppressed = 1 };
        raid.Report(0, () => rs);
        if (!messages.Any(m => m.StartsWith("AFKRaidWatch:") && m.Contains("candidates_excluded_total=1"))) throw new Exception("Raid summary missing");
        if (messages.Where(m => m.StartsWith("AFKRaidWatch:")).Any(m => m.Contains("sleep_"))) throw new Exception("Raid output includes sleep");
        var inactive = new AFKRaidWatchDiagnostics(log, policy); count = messages.Count;
        inactive.Report(0, () => new RaidWatchStatus { Connected = 3 }); inactive.Report(60, () => new RaidWatchStatus { Connected = 0, Candidates = 1 });
        if (messages.Count != count) throw new Exception("Inactive raid scope logged");
        System.Console.WriteLine("PASS: independent sleep/raid diagnostic scope, lazy gating, semantic cadence, compact heartbeat, quiet empty/inactive scope and outcome uncertainty.");
    }
}
