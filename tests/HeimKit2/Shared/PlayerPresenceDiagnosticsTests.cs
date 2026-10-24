using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Core.Players;
internal static class PlayerPresenceDiagnosticsTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Presence notice checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, e) => records.Add((e.Level, LogCapture.Text(e.Data)));
        var notices = new PlayerPresenceDiagnostics(log);
        try
        {
            // Ordinary notices remain available when the empty-server Debug cutoff is active.
            DiagnosticLog.ServerActivity.Reset(); DiagnosticLog.ServerActivity.Observe(300, false);
            notices.Observe(null, true);
            if (records.Count != 0) throw new Exception("Unavailable startup population announced empty");
            notices.Observe(false, false);
            if (records.Count != 0) throw new Exception("Empty population claimed completed cleanup");
            notices.Observe(false, true); notices.Observe(false, true); notices.Observe(null, true); notices.Observe(false, true);
            if (records.Count != 1 || records[0].Text != "PlayerPresence: No players have joined the world; all features are inactive until a player joins.") throw new Exception("Initial empty notice missing or repeated");
            notices.Observe(true, true); notices.Observe(true, true); notices.Observe(null, true); notices.Observe(true, true);
            if (records.Count != 2 || records[^1].Text != "PlayerPresence: Players have joined the world; all enabled features are active.") throw new Exception("First connection missing or subsequent connections repeated");
            notices.Observe(false, true); notices.Observe(false, true); notices.Observe(true, true);
            if (records.Count != 4 || records.Any(r => r.Level != LogLevel.Info)) throw new Exception("Departure/reconnect notices or severity incorrect");
            records.Clear();
            var alreadyOccupied = new PlayerPresenceDiagnostics(log);
            alreadyOccupied.Observe(true, true); alreadyOccupied.Observe(true, true);
            if (records.Count != 1) throw new Exception("Initial occupied population did not establish one known state");
        }
        finally { DiagnosticLog.ServerActivity.Reset(); }
        System.Console.WriteLine("PASS: one initial/changed empty or occupied Info notice, quiet additional connections and polls, unknown population retained, reconnect notices and independence from Debug pause.");
    }
}
