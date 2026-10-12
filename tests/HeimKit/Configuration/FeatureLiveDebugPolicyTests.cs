using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKNightWatch.Models;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.AFKRaidWatch.Models;
using HeimKit.Shared;

internal static class FeatureLiveDebugPolicyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        using var log = new ManualLogSource("Other feature live policy checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, e) => records.Add((e.Level, e.Data.ToString()!));
        string[] names = { "InfiniteTorches", "AFKNightWatch", "AFKRaidWatch" };
        foreach (int selected in Enumerable.Range(0, 3))
        {
            DiagnosticLog.StartSession();
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var torch = new InfiniteTorchesSettings(config, log);
            var sleep = new AFKNightWatchSettings(config, log);
            var raid = new AFKRaidWatchSettings(config, log);
            var global = DiagnosticsSettings.Bind(config); global.Value = false;
            ConfigEntry<bool>[] switches = { torch.DebugMode, sleep.DebugMode, raid.DebugMode };
            DebugLoggingPolicy[] policies = { torch.DebugLogging, sleep.DebugLogging, raid.DebugLogging };
            // Match production construction: torch uses a live callback; both AFK modules retain policy.
            var td = new InfiniteTorchesDiagnostics(() => torch.DebugLogging.Enabled, log);
            var sd = new AFKNightWatchDiagnostics(log, sleep.DebugLogging);
            var rd = new AFKRaidWatchDiagnostics(log, raid.DebugLogging);
            Action<float>[] emit = {
                now => td.Report(now, () => "Pending refill evidence; examined_pass=1"),
                now => sd.Report(now, () => new NightWatchStatus { Players = 3, Votes = 1,
                    Vote = new SleepDecision { Participants = 3, Sleepers = 2, Required = 2, Met = true } }),
                now => rd.Report(now, () => new RaidWatchStatus { Connected = 3, Candidates = 1, Suppressed = 1 })
            };
            records.Clear(); foreach (var write in emit) write(0);
            Check(records.Count == 0, "All-off policies emitted Debug");
            switches[selected].Value = true;
            foreach (var write in emit) write(1);
            Check(records.Count > 0 && records.All(r => r.Level == LogLevel.Debug && r.Text.StartsWith(names[selected] + ":")),
                "Feature-only enablement failed or enabled another feature: " + names[selected]);
            switches[selected].Value = false; records.Clear(); emit[selected](62);
            Check(!policies[selected].Enabled && records.Count == 0, "Feature retained enabled snapshot: " + names[selected]);
            global.Value = true; emit[selected](63);
            Check(records.Any(r => r.Level == LogLevel.Debug), "Global inheritance failed: " + names[selected]);
            global.Value = false; switches[selected].Value = true; records.Clear();
            DiagnosticLog.ServerActivity.Observe(300, false); emit[selected](124);
            Check(records.Count == 0, "Feature bypassed empty Debug grace: " + names[selected]);
            DiagnosticLog.ServerActivity.Observe(301, true); emit[selected](125);
            Check(records.Any(r => r.Level == LogLevel.Debug), "Feature-only output failed to resume: " + names[selected]);
        }
        DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: InfiniteTorches, AFKNightWatch and AFKRaidWatch retain live feature-only debug policies, isolate output, disable immediately, inherit global debug and recover after empty grace.");
    }
}
