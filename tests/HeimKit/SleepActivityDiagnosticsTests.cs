using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.MajoritySleep;
using HeimKit.Features.AfkSleepExclusion;
using HeimKit.Features.AfkRaidProtection;
using HeimKit.Shared;
internal static class SleepActivityDiagnosticsTests
{
    public static void Run()
    {
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "activity-log-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = DiagnosticsSettings.Bind(config);
        var policy = new DebugLoggingPolicy(debug, false);
        using var logger = new ManualLogSource("Activity log checks");
        var messages = new List<string>(); logger.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
        var majority = new MajoritySleepDiagnostics(logger, policy);
        var exclusion = new AfkSleepExclusionDiagnostics(logger, policy);
        var raid = new AfkRaidProtectionDiagnostics(logger, policy);
        majority.Report(0, null, 0); exclusion.Report(0, null, 0);
        exclusion.Players(0, Enumerable.Range(0, 20).Select(i => new PlayerActivityView { Peer = i }));
        raid.Report(0, 2, 1, 0, 0); raid.Sample(0, "native", default, true, "all_nearby_players_inferred_idle");
        if (messages.Count != 0) throw new Exception("Debug-off optional logs leaked");
        debug.Value = true;
        majority.Report(0, new SleepDecision { Participants = 3, Sleepers = 2, Required = 2, Met = true }, 1);
        if (!messages.Single().Contains("quorum_met_last_vote=true") || !messages[0].Contains("time_skip=not_observed")) throw new Exception("Vote was confused with actual skip");
        majority.Report(59, null, 0); if (messages.Count != 1) throw new Exception("Sleep summary flooded");
        majority.Report(60, new SleepDecision { Unknown = true }, 2);
        if (!messages[^1].Contains("native_vote_fallback=false") || !messages[^1].Contains("no known living bed sleeper")) throw new Exception("Missing sleeper misreported native fallback");
        exclusion.Players(0, Enumerable.Range(0, 20).Select(i => new PlayerActivityView { Peer = i }));
        int count = messages.Count; if (count != 8) throw new Exception("AFK samples exceeded six");
        exclusion.Players(59, new[] { new PlayerActivityView() }); if (messages.Count != count) throw new Exception("AFK sampling interval failed");
        for (int i = 0; i < 50; i++) raid.Sample(0, "native", new UnityEngine.Vector3(i, 0, 0), false, "nearby_active_player");
        if (messages.Count != count + 6) throw new Exception("Raid sample budget failed");
        raid.Report(0, 2, 1, 50, 0);
        if (!messages[^1].Contains("omitted_messages_interval=RESULT:44")) throw new Exception("Raid omission evidence missing");
        debug.Value = false; count = messages.Count;
        majority.Report(120, null, 0); exclusion.Report(120, null, 0); raid.Report(120, 2, 1, 50, 0);
        if (messages.Count != count) throw new Exception("Runtime debug-off leaked output");
        System.Console.WriteLine("PASS: new module diagnostics respect master gating, bound summaries/actor/candidate samples and distinguish quorum, native fallback, suppression and unobserved time/event outcomes.");
    }
}
