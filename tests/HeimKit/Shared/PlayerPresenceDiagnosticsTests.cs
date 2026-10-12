using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Shared.Players;
internal static class PlayerPresenceDiagnosticsTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Presence notice checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, e) => records.Add((e.Level, e.Data.ToString()!));
        var notices = new PlayerPresenceDiagnostics(log);
        try
        {
            // Ordinary notices remain available when the empty-server Debug cutoff is active.
            DiagnosticLog.ServerActivity.Reset(); DiagnosticLog.ServerActivity.Observe(300, false);
            notices.Observe(null);
            if (records.Count != 0) throw new Exception("Unavailable startup population announced empty");
            notices.Observe(false); notices.Observe(false); notices.Observe(null); notices.Observe(false);
            if (records.Count != 1 || records[0].Text != "PlayerPresence: Server is empty.") throw new Exception("Initial empty notice missing or repeated");
            notices.Observe(true); notices.Observe(true); notices.Observe(null); notices.Observe(true);
            if (records.Count != 2 || records[^1].Text != "PlayerPresence: Server has connected players.") throw new Exception("First connection missing or subsequent connections repeated");
            notices.Observe(false); notices.Observe(false); notices.Observe(true);
            if (records.Count != 4 || records.Any(r => r.Level != LogLevel.Info)) throw new Exception("Departure/reconnect notices or severity incorrect");
            records.Clear();
            var alreadyOccupied = new PlayerPresenceDiagnostics(log);
            alreadyOccupied.Observe(true); alreadyOccupied.Observe(true);
            if (records.Count != 1) throw new Exception("Initial occupied population did not establish one known state");
        }
        finally { DiagnosticLog.ServerActivity.Reset(); }
        System.Console.WriteLine("PASS: one initial/changed empty or occupied Info notice, quiet additional connections and polls, unknown population retained, reconnect notices and independence from Debug pause.");
    }
}
