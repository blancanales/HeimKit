using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKNightWatch.Models;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;
using HeimKit.Shared;

internal static class ServerDebugActivityTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var gate = new ServerDebugActivity();
        gate.Observe(299.9, false); Check(gate.Allowed, "Startup grace ended early");
        gate.Observe(300, false); Check(!gate.Allowed, "Empty-server cutoff did not occur at five minutes");
        gate.Observe(900, false); Check(!gate.Allowed, "Empty server resumed without players");
        gate.Observe(901, true); Check(gate.Allowed, "Player connection did not resume immediately");
        gate.Observe(950, null); Check(gate.Allowed, "Unknown population was treated as a disconnect");
        gate.Observe(1000, false); gate.Observe(1299.9, false);
        Check(gate.Allowed, "Disconnect grace ended early");
        gate.Observe(1300, false); Check(!gate.Allowed, "Disconnect grace did not expire");
        gate.Observe(1301, true); gate.Observe(1400, false); gate.Observe(1600, true);
        gate.Observe(1700, false); gate.Observe(1999, false);
        Check(gate.Allowed, "Reconnection did not reset the empty interval");
        gate.Observe(2000, false); Check(!gate.Allowed, "New empty interval did not expire");
        gate.Observe(0, false); Check(gate.Allowed, "Clock rollback did not reset the grace period");
        gate.Reset(); gate.Observe(300, null); Check(!gate.Allowed, "Unavailable startup context kept debug alive forever");

        using var logger = new ManualLogSource("Server debug activity checks");
        var records = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, args) => records.Add((args.Level, args.Data.ToString()!));
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "idle-debug-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = DiagnosticsSettings.Bind(config); debug.Value = true;
        var policy = new DebugLoggingPolicy(debug, false);
        var torch = new InfiniteTorchesDiagnostics(policy, logger);
        var weather = new WeatherGuardDiagnostics(logger, true);
        var afk = new AFKNightWatchDiagnostics(logger, policy);
        try
        {
            DiagnosticLog.ServerActivity.Reset(); DiagnosticLog.ServerActivity.Observe(300, false);
            Check(policy.ConfiguredEnabled && !policy.Enabled && debug.Value, "Idle suppression changed configured debug settings");
            torch.Report(300, () => throw new Exception("Idle torch snapshot evaluated"));
            weather.Report(300, () => throw new Exception("Idle weather snapshot evaluated"));
            var medium = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium);
            medium.Report(300, () => throw new Exception("Idle medium snapshot evaluated"),
                protection: () => throw new Exception("Idle medium protection snapshot evaluated"));
            afk.Report(300, () => throw new Exception("Idle AFK snapshot evaluated"));
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Detail, "Suppressed", InvalidFields(), LogLevel.Debug);
            Check(records.Count == 0, "Automatic diagnostics emitted during idle suppression");
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Lifecycle, "Ready.");
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Lifecycle, "Warning", level: LogLevel.Warning);
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Lifecycle, "Error", level: LogLevel.Error);
            weather.Coverage(new CoverageSnapshot { State = "module_inactive" });
            Check(records.Count == 4 && records[1].Level == LogLevel.Warning && records[2].Level == LogLevel.Error &&
                records[3].Text.Contains("Requested coverage report"), "Idle suppression hid ordinary output or requested coverage");
            weather.Command(new WearObservation(), null, true);
            weather.Wear(new WearObservation(), null, true);
            Check(records.Count == 6 && records.Skip(4).All(r => r.Level == LogLevel.Error),
                "Idle pause hid a failure captured by an in-flight weather observation");
            // Constructing a diagnostics instance while paused must not freeze it off.
            var createdWhilePaused = new WeatherGuardDiagnostics(logger, policy.ConfiguredEnabled);
            DiagnosticLog.ServerActivity.Observe(301, true);
            records.Clear();
            torch.Report(301, () => "Resumed");
            weather.Report(301, () => new WeatherGuardStatus());
            createdWhilePaused.Report(301, () => new WeatherGuardStatus());
            afk.Report(301, () => new NightWatchStatus { Players = 1, Vote = new SleepDecision { Participants = 1, Sleepers = 1 } });
            Check(records.Count == 7 && records.All(r => r.Level == LogLevel.Debug), "All feature diagnostics must resume without reconfiguration");
            Check(!new DebugLoggingPolicy(debug, true).Enabled, "Reconnect bypassed hidden suppression");
            debug.Value = false; Check(!policy.Enabled, "Reconnect bypassed configured debug-off");
        }
        finally { DiagnosticLog.ServerActivity.Reset(); }
        System.Console.WriteLine("PASS: five-minute startup/disconnect cutoff, reconnect/reset/unknown-clock behavior, lazy feature suppression, retained warnings/errors/reports and automatic resumption.");
    }
    private static IEnumerable<(string Key, object? Value)> InvalidFields()
    {
        yield return ("unused", 1);
        throw new Exception("Suppressed debug formatted fields");
    }
}
