using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;

internal static class PlayerActivityNoticesTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        using var log = new ManualLogSource("AFK notice checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => records.Add((args.Level, LogCapture.Text(args.Data)));
        try
        {
            DiagnosticLog.ServerActivity.Reset(); DiagnosticLog.ServerActivity.Observe(300, false);
            var notices = new PlayerActivityNotices(log);
            var a = new PlayerActivityView { Peer = 1, Name = "Trevor", Session = 1, Known = true, Alive = true };
            var b = new PlayerActivityView { Peer = 2, Name = "Trevor", Session = 2, Known = true, Alive = true };
            notices.Observe(new[] { a, b }, 3); Check(records.Count == 0, "Initial active players emitted AFK notices.");
            a.Idle = true; notices.Observe(new[] { a, b }, 3);
            Check(records.Count == 1 && records[^1].Text.Contains("Player \"Trevor\" (peer 1) became AFK") && records[^1].Text.Contains("1 of 3 joined"), "Named AFK entry/count missing.");
            notices.Observe(new[] { a, b }, 3); Check(records.Count == 1, "Unchanged AFK state repeated.");
            b.Idle = true; notices.Observe(new[] { a, b }, 3);
            Check(records.Count == 2 && records[^1].Text.Contains("(peer 2)") && records[^1].Text.Contains("2 of 3 joined"), "Duplicate names or second idle count were ambiguous.");
            a.Idle = false; notices.Observe(new[] { a, b }, 3);
            Check(records.Count == 3 && records[^1].Text.Contains("left AFK status (activity detected)") && records[^1].Text.Contains("1 of 3 joined"), "Return to activity/count missing.");
            notices.Observe(new[] { a }, 2);
            Check(records.Count == 4 && records[^1].Text.Contains("disconnected or no longer observed") && records[^1].Text.Contains("0 of 2 joined"), "Departure retained a stale AFK count or claimed activity.");
            a.Idle = true; a.Name = "Line\nBreak\tName"; notices.Observe(new[] { a }, 2);
            Check(!records[^1].Text.Contains('\n') && !records[^1].Text.Contains('\t'), "Player name injected extra log lines.");
            a.Known = false; notices.Observe(new[] { a }, null);
            Check(records[^1].Text.Contains("player data unavailable") && records[^1].Text.Contains("joined count unavailable"), "Unknown data/count misrepresented.");
            a.Known = true; a.Idle = true; a.Name = ""; notices.Observe(new[] { a }, 1);
            Check(records[^1].Text.Contains("Peer 1 became AFK"), "Missing name lost specific peer identity.");
            a.Alive = false; notices.Observe(new[] { a }, 1);
            Check(records[^1].Text.Contains("player died"), "Death was logged as detected activity.");
            a.Alive = true; a.Idle = true; notices.Observe(new[] { a }, 1);
            notices.Reset("activity observations unavailable", 1);
            Check(records[^1].Text.Contains("Current AFK players: unavailable (tracking stopped)"), "Tracking loss reported a false known count.");
            int before = records.Count; notices.Reset("activity observations unavailable", 1); notices.Observe(new[] { a = new() { Peer = 1, Session = 3, Known = true, Alive = true } }, 1);
            Check(records.Count == before, "Repeated reset or recovery fabricated an AFK transition.");
            a.Idle = true; notices.Observe(new[] { a }, 1); notices.Reset("server empty", 0);
            Check(records[^1].Text.Contains("0 of 0 joined"), "Empty server did not clear AFK count.");
            Check(records.All(r => r.Level == LogLevel.Info), "AFK notices require debugging or have incorrect severity.");
            records.Clear();
            var presence = new Presence { Count = 2 };
            bool fail = false; float x = 0;
            using var service = new PlayerActivityService(new(300, 60, .5f, 10, 1), log, presence, () => true,
                () => fail ? throw new InvalidOperationException("read failed") : new() { new PlayerActivityObservation { Peer = 10, Name = "Tester", Character = "a", Known = true, Alive = true, Health = 100, X = x } }, on => { });
            var first = service.Acquire(() => false); var second = service.Acquire(() => false);
            for (int now = 0; now < 300; now++) { service.Tick(now); service.Snapshot(now); }
            Check(records.Count == 0, "AFK entry arrived before timeout or active polls emitted notices.");
            service.Tick(300); service.Snapshot(300);
            Check(records.Count == 1 && records[^1].Text.Contains("Tester") && records[^1].Text.Contains("1 of 2 joined"), "Shared detector failed to emit one debug-off AFK entry for both consumers.");
            x = 1; service.Tick(301); service.Snapshot(301);
            Check(records.Count == 2 && records[^1].Text.Contains("left AFK status"), "Observed movement did not emit one active transition.");
            for (int now = 302; now <= 601; now++) service.Tick(now);
            Check(records.Count == 3 && records[^1].Text.Contains("became AFK"), "Second AFK entry lost.");
            first.Dispose(); Check(records.Count == 3, "Removing one consumer reset another's AFK state.");
            fail = true; service.Tick(602);
            Check(records.Count(r => r.Level == LogLevel.Info && r.Text.Contains("left AFK status")) == 2 && records.Any(r => r.Text.Contains("unavailable (tracking stopped)")), "Sampling failure did not clear/log AFK state.");
            fail = false; service.Tick(603);
            Check(!service.Snapshot(603).Single().Idle, "Recovery retained idle state.");
            second.Dispose(); service.Dispose();
        }
        finally { DiagnosticLog.ServerActivity.Reset(); }
        System.Console.WriteLine("PASS: named Info AFK entry/exit and current counts without debugging, quiet polls, duplicate-name identity, departures/death/unknown/reset accuracy, single shared stream and default five-minute detector transitions.");
    }
    private sealed class Presence : IPlayerPresence
    {
        public int? JoinedCount => ConnectedCount;
        public bool? HasJoinedPlayers => HasConnectedPlayers;
        public int? Count;
        public int? ConnectedCount => Count;
        public int? ReadyCount => Count;
        public bool? HasConnectedPlayers => Count.HasValue ? Count > 0 : null;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() => Count = null;
    }
}
