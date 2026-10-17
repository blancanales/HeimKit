using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;

internal static class PlayerActivityHealthTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static PlayerActivityObservation Actor(float health, float x = 0, bool alive = true) => new()
    {
        Peer = 1, Name = "Stationary", Character = "same", Known = true, Alive = alive,
        Health = health, X = x
    };

    public static void Run()
    {
        // Constant health, gradual food-related decline, regeneration and repeated damage
        // must all reach the same timeout when no other activity is observed.
        foreach (Func<int, float> healthAt in new Func<int, float>[]
        {
            t => 150,
            t => 150 - t * .1f,
            t => 75 + t * .1f,
            t => 150 - t
        })
        {
            var tracker = new PlayerActivityTracker(new(60, 0, .5f, 10, 1));
            for (int t = 0; t <= 65; t++)
            {
                var view = tracker.Update(t, new[] { Actor(healthAt(t)) }).Single();
                Check(view.Idle == (t >= 60), "Health changes must not alter the ordinary idle boundary.");
                Check(view.LastActivityAt == 0 && view.LastSignal == "none_observed", "Health change invented activity evidence.");
            }
            var hurt = tracker.Update(66, new[] { Actor(10) }).Single();
            Check(hurt.Idle && hurt.LastActivityAt == 0, "Sudden damage or food expiration must not wake an idle player.");
            var moved = tracker.Update(67, new[] { Actor(10, x: 1) }).Single();
            Check(!moved.Idle && moved.LastActivityAt == 67 && moved.LastSignal == "movement", "Movement must still wake an injured idle player.");
            for (int t = 68; t <= 127; t++)
                Check(tracker.Update(t, new[] { Actor(10, x: 1) }).Single().Idle == (t == 127), "Movement must start the normal fresh idle interval.");
            var dead = tracker.Update(128, new[] { Actor(0, x: 1, alive: false) }).Single();
            Check(!dead.Idle && dead.Reason == "dead_or_loading", "Death must retain its existing eligibility behavior.");
            Check(!tracker.Update(129, new[] { Actor(100, x: 1) }).Single().Idle, "Respawn must retain fresh recovery grace.");
        }

        NoticesWithDebugOff();
        System.Console.WriteLine("PASS: food decline, regeneration and damage do not reset AFK; movement, idle recovery, death/respawn and Info notices remain independent of Debug.");
    }

    private static void NoticesWithDebugOff()
    {
        using var log = new ManualLogSource("Health activity notices");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, e) => records.Add((e.Level, e.Data.ToString()!));
        try
        {
            DiagnosticLog.ServerActivity.Reset();
            DiagnosticLog.ServerActivity.Observe(300, false);
            float health = 150, x = 0;
            using var service = new PlayerActivityService(new(60, 0, .5f, 10, 1), log, new Presence(), () => true,
                () => new() { Actor(health, x) }, _ => { });
            using var first = service.Acquire(() => false);
            using var second = service.Acquire(() => false);
            for (int t = 0; t <= 65; t++)
            {
                health = 150 - t * .1f;
                service.Tick(t); service.Snapshot(t);
                Check(records.Count == (t >= 60 ? 1 : 0), "Food decay must produce exactly one timely shared AFK notice with Debug off.");
            }
            Check(records[0].Text.Contains("became AFK") && records[0].Text.Contains("1 of 1 joined"), "AFK entry identity/count missing.");
            health = 10;
            service.Tick(66); service.Snapshot(66);
            Check(records.Count == 1 && service.Snapshot(66).Single().Idle, "Damage must not produce an AFK exit.");
            x = 1;
            service.Tick(67); service.Snapshot(67);
            Check(records.Count == 2 && records[1].Text.Contains("left AFK status (activity detected)") && records[1].Text.Contains("0 of 1 joined"),
                "Movement must produce exactly one AFK exit with Debug off.");
            for (int t = 68; t <= 127; t++) service.Tick(t);
            Check(records.Count == 3 && records[2].Text.Contains("became AFK"), "Ordinary idle re-entry after movement lost.");
            Check(records.All(r => r.Level == LogLevel.Info), "AFK transitions changed severity or required Debug output.");
        }
        finally { DiagnosticLog.ServerActivity.Reset(); }
    }

    private sealed class Presence : IPlayerPresence
    {
        public int? JoinedCount => 1;
        public int? ConnectedCount => 1;
        public int? ReadyCount => 1;
        public bool? HasJoinedPlayers => true;
        public bool? HasConnectedPlayers => true;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { }
    }
}
